package nativebridge

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/beilunyang/openhongguo/internal/history"
	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

type bridgeServiceStub struct {
	discover  func(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error)
	search    func(context.Context, string, sdk.SearchOptions) (*sdk.Page[model.SeriesSummary], error)
	detail    func(context.Context, []string) ([]model.SeriesDetail, error)
	recommend func(context.Context, string) (*model.Recommend, error)
	playback  func(context.Context, string, sdk.PlaybackOptions) (*model.Playback, error)
}

func (s *bridgeServiceStub) Discover(ctx context.Context, options sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
	if s.discover != nil {
		return s.discover(ctx, options)
	}
	return nil, nil
}

func (s *bridgeServiceStub) Search(ctx context.Context, query string, options sdk.SearchOptions) (*sdk.Page[model.SeriesSummary], error) {
	if s.search != nil {
		return s.search(ctx, query, options)
	}
	return nil, nil
}

func (s *bridgeServiceStub) GetSeriesBatch(ctx context.Context, ids []string) ([]model.SeriesDetail, error) {
	if s.detail != nil {
		return s.detail(ctx, ids)
	}
	return nil, nil
}

func (s *bridgeServiceStub) SeriesRecommend(ctx context.Context, id string) (*model.Recommend, error) {
	if s.recommend != nil {
		return s.recommend(ctx, id)
	}
	return nil, nil
}

func (s *bridgeServiceStub) ResolvePlayback(ctx context.Context, id string, options sdk.PlaybackOptions) (*model.Playback, error) {
	if s.playback != nil {
		return s.playback(ctx, id, options)
	}
	return nil, sdk.ErrContentUnavailable
}

func bridgeRequest(t *testing.T, method string, args any) Request {
	t.Helper()
	raw, err := json.Marshal(args)
	if err != nil {
		t.Fatal(err)
	}
	return Request{ID: "test", Method: method, Args: raw}
}

func bridgeItem(id string) model.SeriesSummary {
	return model.SeriesSummary{ID: id, Title: "短剧 " + id, CoverURL: "https://images.invalid/" + id + ".jpg"}
}

func bridgeIDs(items []model.SeriesSummary) []string {
	ids := make([]string, 0, len(items))
	for _, item := range items {
		ids = append(ids, item.ID)
	}
	return ids
}

func TestDiscoverAggregatesPagesAndKeepsCursor(t *testing.T) {
	var calls []sdk.DiscoverOptions
	service := &bridgeServiceStub{discover: func(_ context.Context, options sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
		calls = append(calls, options)
		switch options.Cursor {
		case "":
			return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{bridgeItem("1001"), bridgeItem("1002"), bridgeItem("bad")}, Cursor: "page-2", HasMore: true}, nil
		case "page-2":
			return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{bridgeItem("1002"), bridgeItem("1003")}, Cursor: "page-3", HasMore: true}, nil
		case "page-3":
			return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{bridgeItem("1004")}, Cursor: "page-4", HasMore: true}, nil
		default:
			return nil, fmt.Errorf("出现意外游标 %q", options.Cursor)
		}
	}}
	server := &Server{Service: service}
	page, err := server.discover(context.Background(), "", "", 24)
	if err != nil {
		t.Fatal(err)
	}
	if got := bridgeIDs(page.Items); !reflect.DeepEqual(got, []string{"1001", "1002", "1003", "1004"}) {
		t.Fatalf("跨页去重结果=%v", got)
	}
	wantCalls := []sdk.DiscoverOptions{{Channel: "推荐"}, {Channel: "推荐", Cursor: "page-2"}, {Channel: "推荐", Cursor: "page-3"}}
	if !reflect.DeepEqual(calls, wantCalls) || page.Cursor != "page-4" || !page.HasMore {
		t.Fatalf("分页未保持续页位置：calls=%+v page=%+v", calls, page)
	}
}

func TestDiscoverStopsAtMinimumOrExhaustion(t *testing.T) {
	for _, test := range []struct {
		name     string
		cursor   string
		count    int
		hasMore  bool
		wantMore bool
	}{
		{name: "已达到本次推荐数量", cursor: "next", count: 24, hasMore: true, wantMore: true},
		{name: "服务端结束", cursor: "last", count: 2},
		{name: "无续页令牌", count: 2, hasMore: true},
		{name: "续页令牌未前进", cursor: "initial", count: 2, hasMore: true},
	} {
		t.Run(test.name, func(t *testing.T) {
			calls := 0
			server := &Server{Service: &bridgeServiceStub{discover: func(_ context.Context, options sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
				calls++
				if options.Channel != "精选" || options.Cursor != "initial" {
					t.Errorf("续页参数=%+v", options)
				}
				items := make([]model.SeriesSummary, test.count)
				for i := range items {
					items[i] = bridgeItem(fmt.Sprint(1000 + i))
				}
				return &sdk.Page[model.SeriesSummary]{Items: items, Cursor: test.cursor, HasMore: test.hasMore}, nil
			}}}
			page, err := server.discover(context.Background(), "精选", "initial", 24)
			if err != nil || calls != 1 || page.HasMore != test.wantMore {
				t.Fatalf("终止条件不正确：calls=%d page=%+v err=%v", calls, page, err)
			}
		})
	}
}

func TestDiscoverKeepsPartialResultsAfterLaterPageError(t *testing.T) {
	calls := 0
	server := &Server{Service: &bridgeServiceStub{discover: func(_ context.Context, _ sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
		calls++
		if calls == 1 {
			return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{bridgeItem("1001")}, Cursor: "retry-here", HasMore: true}, nil
		}
		return nil, sdk.ErrTransport
	}}}
	page, err := server.discover(context.Background(), "", "", 24)
	if err != nil || len(page.Items) != 1 || page.Cursor != "retry-here" || !page.HasMore {
		t.Fatalf("后续页失败丢失已有结果或重试位置：page=%+v err=%v", page, err)
	}
	server.Service = &bridgeServiceStub{discover: func(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
		return nil, sdk.ErrTransport
	}}
	if _, err := server.discover(context.Background(), "", "", 24); !errors.Is(err, sdk.ErrTransport) {
		t.Fatalf("首页失败未传递：%v", err)
	}
}

func TestSearchForwardsQueryAndCursor(t *testing.T) {
	server := &Server{Service: &bridgeServiceStub{search: func(_ context.Context, query string, options sdk.SearchOptions) (*sdk.Page[model.SeriesSummary], error) {
		if query != "测试短剧" || options.Cursor != "search-20" || options.Tab != "剧集" || options.PageSize != 20 {
			t.Errorf("搜索参数不正确：query=%q options=%+v", query, options)
		}
		return nil, nil
	}}}
	result, err := server.Dispatch(context.Background(), bridgeRequest(t, "search", map[string]string{"query": "  测试短剧  ", "cursor": "search-20"}))
	if err != nil {
		t.Fatal(err)
	}
	page := result.(*sdk.Page[model.SeriesSummary])
	if page.Items == nil || len(page.Items) != 0 || page.HasMore {
		t.Fatalf("空搜索结果没有规范化：%+v", page)
	}
}

func TestRecommendMergesRelatedOnceAndOmitsCurrentSeries(t *testing.T) {
	relatedCalls := 0
	server := &Server{Service: &bridgeServiceStub{
		discover: func(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
			return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{bridgeItem("1001"), bridgeItem("1002"), bridgeItem("1003")}, Cursor: "next", HasMore: true}, nil
		},
		recommend: func(_ context.Context, id string) (*model.Recommend, error) {
			relatedCalls++
			if id != "1001" {
				t.Errorf("当前剧集=%q", id)
			}
			return &model.Recommend{Items: []model.SeriesSummary{bridgeItem("1002"), bridgeItem("1001"), bridgeItem("1004"), bridgeItem("bad")}}, nil
		},
	}}
	result, err := server.Dispatch(context.Background(), bridgeRequest(t, "recommend", map[string]string{"series_id": "1001"}))
	if err != nil {
		t.Fatal(err)
	}
	page := result.(*recommendationPage)
	if got := bridgeIDs(page.Items); !reflect.DeepEqual(got, []string{"1002", "1004", "1003"}) {
		t.Fatalf("相关推荐排序或去重不正确：%v", got)
	}
	if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "recommend", map[string]string{"series_id": "1001", "cursor": page.Cursor})); err != nil {
		t.Fatal(err)
	}
	if relatedCalls != 1 {
		t.Fatalf("续页重复请求相关推荐：次数=%d", relatedCalls)
	}
}

func TestCoverHydrationKeepsActualSeriesPoster(t *testing.T) {
	const poster = "https://images.invalid/detail.HEIF?signature=unchanged%2Bvalue"
	for _, test := range []struct {
		name      string
		cover     string
		wantCover string
		wantCalls int
	}{
		{name: "列表缺封面", wantCover: poster, wantCalls: 1},
		{name: "HEIC", cover: "https://images.invalid/cover.heic?signature=abc", wantCover: "https://images.invalid/cover.heic?signature=abc"},
		{name: "HEIF大小写", cover: "https://images.invalid/cover.HEIF?signature=abc", wantCover: "https://images.invalid/cover.HEIF?signature=abc"},
		{name: "AVIF", cover: "https://images.invalid/cover.avif?signature=abc", wantCover: "https://images.invalid/cover.avif?signature=abc"},
		{name: "保留JPEG", cover: "https://images.invalid/cover.jpg?signature=abc", wantCover: "https://images.invalid/cover.jpg?signature=abc"},
		{name: "查询串不是图片格式", cover: "https://images.invalid/cover.jpg?source=.heic", wantCover: "https://images.invalid/cover.jpg?source=.heic"},
	} {
		t.Run(test.name, func(t *testing.T) {
			calls := 0
			server := &Server{Service: &bridgeServiceStub{detail: func(_ context.Context, ids []string) ([]model.SeriesDetail, error) {
				calls++
				if !reflect.DeepEqual(ids, []string{"1001"}) {
					t.Errorf("补图剧集=%v", ids)
				}
				return []model.SeriesDetail{{Series: &model.Series{SeriesSummary: model.SeriesSummary{ID: "1001", CoverURL: poster, EpisodeCount: 32, Description: "详情简介"}}, Episodes: []model.Episode{{Number: 1, CoverURL: "https://images.invalid/frame.image"}}}}, nil
			}}}
			items := []model.SeriesSummary{{ID: "1001", Title: "原始标题", CoverURL: test.cover}}
			server.enrich(context.Background(), items)
			if calls != test.wantCalls || items[0].CoverURL != test.wantCover || items[0].Title != "原始标题" {
				t.Fatalf("补图结果=%+v calls=%d", items[0], calls)
			}
			if calls > 0 && (items[0].EpisodeCount != 32 || items[0].Description != "详情简介") {
				t.Fatalf("详情元数据未补齐：%+v", items[0])
			}
		})
	}
}

func TestCoverHydrationBatchesAndReusesDetails(t *testing.T) {
	var batches [][]string
	server := &Server{Service: &bridgeServiceStub{detail: func(_ context.Context, ids []string) ([]model.SeriesDetail, error) {
		batches = append(batches, append([]string(nil), ids...))
		var details []model.SeriesDetail
		for _, id := range ids {
			details = append(details, model.SeriesDetail{Series: &model.Series{SeriesSummary: model.SeriesSummary{ID: id, CoverURL: "https://images.invalid/" + id + ".png", EpisodeCount: 10}}})
		}
		return details, nil
	}}}
	items := make([]model.SeriesSummary, 23)
	for i := range items {
		items[i].ID = fmt.Sprint(1000 + i)
	}
	server.enrich(context.Background(), items)
	if len(batches) != 3 || len(batches[0]) != 10 || len(batches[1]) != 10 || len(batches[2]) != 3 {
		t.Fatalf("补图没有遵守十部上限：%v", batches)
	}
	for _, item := range items {
		if item.CoverURL == "" || item.EpisodeCount != 10 {
			t.Errorf("详情未补齐：%+v", item)
		}
	}
	items[0].CoverURL = ""
	server.enrich(context.Background(), items)
	if len(batches) != 3 || items[0].CoverURL == "" {
		t.Fatalf("缓存没有复用：batches=%v item=%+v", batches, items[0])
	}
	if _, err := server.detail(context.Background(), items[0].ID); err != nil || len(batches) != 3 {
		t.Fatalf("播放详情未复用补图缓存：batches=%v err=%v", batches, err)
	}
}

func TestSeriesPosterDoesNotBecomeAnEpisodeScreenshot(t *testing.T) {
	for _, test := range []struct {
		name      string
		episodes  []model.Episode
		wantCover string
	}{
		{
			name:      "分集乱序仍优先首集",
			episodes:  []model.Episode{{Number: 2, CoverURL: "https://images.invalid/two.image"}, {Number: 1, CoverURL: "https://images.invalid/one.image?signature=original"}},
			wantCover: "https://images.invalid/poster.heic",
		},
		{
			name:      "首集预览缺失使用其他可解码分集",
			episodes:  []model.Episode{{Number: 1}, {Number: 2, CoverURL: "https://images.invalid/two.image"}},
			wantCover: "https://images.invalid/poster.heic",
		},
		{
			name:      "跳过不兼容分集预览",
			episodes:  []model.Episode{{Number: 1, CoverURL: "https://images.invalid/one.avif"}, {Number: 2, CoverURL: "https://images.invalid/two.heif"}, {Number: 3, CoverURL: "https://images.invalid/three.png"}},
			wantCover: "https://images.invalid/poster.heic",
		},
	} {
		t.Run(test.name, func(t *testing.T) {
			item := model.SeriesSummary{ID: "1001", Title: "列表标题", Description: "已有简介", EpisodeCount: 20}
			detail := model.SeriesDetail{Series: &model.Series{SeriesSummary: model.SeriesSummary{ID: "1001", CoverURL: "https://images.invalid/poster.heic", Description: "详情简介", EpisodeCount: 30}}, Episodes: test.episodes}
			fillSummary(&item, detail)
			if item.CoverURL != test.wantCover || item.Title != "列表标题" || item.Description != "已有简介" || item.EpisodeCount != 20 {
				t.Fatalf("预览选择或元数据保留错误：%+v", item)
			}
		})
	}
}

func TestCoverHydrationRefreshesExpiredCacheAndStopsOnCancellation(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	calls := 0
	server := &Server{
		details: map[string]cachedDetail{"1001": {Detail: model.SeriesDetail{Series: &model.Series{SeriesSummary: model.SeriesSummary{ID: "1001", CoverURL: "https://images.invalid/expired.png"}}}, At: time.Now().Add(-10 * time.Minute)}},
		Service: &bridgeServiceStub{detail: func(_ context.Context, ids []string) ([]model.SeriesDetail, error) {
			calls++
			cancel()
			return nil, context.Canceled
		}},
	}
	items := make([]model.SeriesSummary, 11)
	for i := range items {
		items[i].ID = fmt.Sprint(1001 + i)
	}
	server.enrich(ctx, items)
	if calls != 1 || items[0].CoverURL != "" {
		t.Fatalf("过期缓存被复用或取消后仍请求下一批：calls=%d item=%+v", calls, items[0])
	}
}

func TestCoverHydrationFailureKeepsUsableList(t *testing.T) {
	server := &Server{Service: &bridgeServiceStub{
		discover: func(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
			return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{{ID: "1001", Title: "仍然可选"}}}, nil
		},
		detail: func(context.Context, []string) ([]model.SeriesDetail, error) { return nil, sdk.ErrTransport },
	}}
	page, err := server.discover(context.Background(), "", "", 24)
	if err != nil || len(page.Items) != 1 || page.Items[0].Title != "仍然可选" {
		t.Fatalf("补图失败破坏列表：page=%+v err=%v", page, err)
	}
}

func TestSeriesDetailPreservesPosterWithoutMutatingSDKOrCache(t *testing.T) {
	original := &model.Series{SeriesSummary: model.SeriesSummary{ID: "1001", Title: "测试短剧", CoverURL: "https://images.invalid/poster.heic?signature=original"}}
	const preview = "https://images.invalid/episode-one.image?signature=unchanged"
	calls := 0
	server := &Server{Service: &bridgeServiceStub{detail: func(context.Context, []string) ([]model.SeriesDetail, error) {
		calls++
		return []model.SeriesDetail{{Series: original, Episodes: []model.Episode{{ID: "2001", Number: 1, CoverURL: preview}}}}, nil
	}}}
	for i := 0; i < 2; i++ {
		result, err := server.Dispatch(context.Background(), bridgeRequest(t, "series", map[string]string{"id": "1001"}))
		if err != nil {
			t.Fatal(err)
		}
		detail := result.(model.SeriesDetail)
		if detail.Series.CoverURL != original.CoverURL || detail.Series == original || len(detail.Episodes) != 1 || detail.Episodes[0].ID != "2001" {
			t.Fatalf("原生展示详情没有使用独立封面副本：%+v", detail)
		}
	}
	if calls != 1 || original.CoverURL != "https://images.invalid/poster.heic?signature=original" || server.details["1001"].Detail.Series.CoverURL != original.CoverURL {
		t.Fatalf("展示处理修改了原始SDK或缓存：calls=%d original=%+v cached=%+v", calls, original, server.details["1001"])
	}
}

func TestHistoryValidationAndResumePosition(t *testing.T) {
	store := history.NewStore(filepath.Join(t.TempDir(), "history.json"))
	server := &Server{Service: &bridgeServiceStub{}, History: store}
	valid := history.Entry{SeriesID: "1001", Title: "测试短剧", Episode: 2, Episodes: 30, Position: 42.75, UpdatedAt: time.Unix(1, 0)}
	for _, test := range []struct {
		name   string
		change func(*history.Entry)
	}{
		{name: "无效剧集ID", change: func(e *history.Entry) { e.SeriesID = "../1001" }},
		{name: "零分集", change: func(e *history.Entry) { e.Episode = 0 }},
		{name: "负集数", change: func(e *history.Entry) { e.Episodes = -1 }},
		{name: "分集超过已知总集数", change: func(e *history.Entry) { e.Episode = e.Episodes + 1 }},
		{name: "负进度", change: func(e *history.Entry) { e.Position = -0.1 }},
		{name: "超长标题", change: func(e *history.Entry) { e.Title = strings.Repeat("x", 1001) }},
	} {
		t.Run(test.name, func(t *testing.T) {
			entry := valid
			test.change(&entry)
			if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "history_record", entry)); !errors.Is(err, errInvalidInput) {
				t.Fatalf("无效历史未被拒绝：%v", err)
			}
		})
	}
	for _, raw := range []string{`{"series_id":"1001","episode":1,"position":1e999}`, `{"series_id":"1001","episode":1,"position":NaN}`} {
		if _, err := server.Dispatch(context.Background(), Request{Method: "history_record", Args: json.RawMessage(raw)}); !errors.Is(err, errInvalidInput) {
			t.Errorf("异常数值未被拒绝：%v", err)
		}
	}
	entries, err := store.List(context.Background())
	if err != nil || len(entries) != 0 {
		t.Fatalf("无效记录写入了文件：%+v err=%v", entries, err)
	}
	before := time.Now().UTC()
	if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "history_record", valid)); err != nil {
		t.Fatal(err)
	}
	result, err := server.Dispatch(context.Background(), bridgeRequest(t, "history", nil))
	if err != nil {
		t.Fatal(err)
	}
	entries = result.([]history.Entry)
	if len(entries) != 1 || entries[0].Position != valid.Position || entries[0].Episode != 2 || entries[0].UpdatedAt.Before(before) {
		t.Fatalf("续播信息或服务器时间错误：%+v", entries)
	}
	canceled, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := server.Dispatch(canceled, bridgeRequest(t, "history_record", valid)); !errors.Is(err, context.Canceled) {
		t.Fatalf("历史写入丢失取消信号：%v", err)
	}
	valid.Episodes = 0
	if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "history_record", valid)); err != nil {
		t.Fatalf("未知总集数不应影响保存进度：%v", err)
	}
}

type bridgePipeRun struct {
	input   *io.PipeWriter
	output  bytes.Buffer
	written chan struct{}
	done    chan struct{}
	err     error
}

func (run *bridgePipeRun) Write(data []byte) (int, error) {
	n, err := run.output.Write(data)
	if err == nil {
		run.written <- struct{}{}
	}
	return n, err
}

func startBridgePipe(t *testing.T, service Service) *bridgePipeRun {
	t.Helper()
	reader, writer := io.Pipe()
	run := &bridgePipeRun{input: writer, written: make(chan struct{}, 16), done: make(chan struct{})}
	go func() {
		defer close(run.done)
		defer reader.Close()
		run.err = (&Server{Service: service}).Serve(context.Background(), reader, run)
	}()
	t.Cleanup(func() {
		_ = writer.Close()
		waitBridgeSignal(t, run.done, "桥接进程结束")
	})
	return run
}

func waitBridgeSignal(t *testing.T, signal <-chan struct{}, description string) {
	t.Helper()
	select {
	case <-signal:
	case <-time.After(3 * time.Second):
		t.Fatalf("等待%s超时", description)
	}
}

func (run *bridgePipeRun) send(t *testing.T, id, method string, args any) {
	t.Helper()
	raw, err := EncodeRequest(id, method, args)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := run.input.Write(append(raw, '\n')); err != nil {
		t.Fatal(err)
	}
}

func (run *bridgePipeRun) finish(t *testing.T) []response {
	t.Helper()
	_ = run.input.Close()
	waitBridgeSignal(t, run.done, "输入EOF后的任务清理")
	if run.err != nil {
		t.Fatal(run.err)
	}
	decoder := json.NewDecoder(&run.output)
	var responses []response
	for {
		var value response
		err := decoder.Decode(&value)
		if errors.Is(err, io.EOF) {
			return responses
		}
		if err != nil {
			t.Fatal(err)
		}
		responses = append(responses, value)
	}
}

func TestServeCancelsRequestWithoutClosingPipe(t *testing.T) {
	started := make(chan struct{})
	canceled := make(chan struct{})
	run := startBridgePipe(t, &bridgeServiceStub{discover: func(ctx context.Context, _ sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
		close(started)
		<-ctx.Done()
		close(canceled)
		return nil, ctx.Err()
	}})
	run.send(t, "slow", "discover", nil)
	waitBridgeSignal(t, started, "发现请求开始")
	run.send(t, "cancel-slow", "cancel", map[string]string{"request_id": "slow"})
	waitBridgeSignal(t, canceled, "显式取消传播到SDK")
	run.send(t, "status", "status", nil)
	// 等两条响应写完再模拟窗口关闭，避免 EOF 取消尚未调度的状态请求。
	waitBridgeSignal(t, run.written, "首条响应写入")
	waitBridgeSignal(t, run.written, "状态响应写入")
	responses := run.finish(t)
	if len(responses) != 2 {
		t.Fatalf("取消消息不应另发响应：%+v", responses)
	}
	for _, value := range responses {
		switch value.ID {
		case "slow":
			if value.Error == nil || value.Error.Code != "CANCELED" {
				t.Errorf("取消响应=%+v", value)
			}
		case "status":
			if value.Error != nil || value.Result == nil {
				t.Errorf("取消影响了其他请求：%+v", value)
			}
		default:
			t.Errorf("意外响应=%+v", value)
		}
	}
}

func TestServeEOFCancelsInFlightRequests(t *testing.T) {
	started := make(chan struct{})
	canceled := make(chan struct{})
	run := startBridgePipe(t, &bridgeServiceStub{discover: func(ctx context.Context, _ sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
		close(started)
		<-ctx.Done()
		close(canceled)
		return nil, ctx.Err()
	}})
	run.send(t, "waiting", "discover", nil)
	waitBridgeSignal(t, started, "发现请求开始")
	responses := run.finish(t)
	waitBridgeSignal(t, canceled, "EOF取消传播到SDK")
	if len(responses) != 1 || responses[0].ID != "waiting" || responses[0].Error == nil || responses[0].Error.Code != "CANCELED" {
		t.Fatalf("EOF清理结果=%+v", responses)
	}
}

func TestServeRejectsDuplicateActiveRequestIDs(t *testing.T) {
	started := make(chan struct{})
	run := startBridgePipe(t, &bridgeServiceStub{discover: func(ctx context.Context, _ sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
		close(started)
		<-ctx.Done()
		return nil, ctx.Err()
	}})
	run.send(t, "duplicate", "discover", nil)
	waitBridgeSignal(t, started, "首个请求开始")
	run.send(t, "duplicate", "status", nil)
	responses := run.finish(t)
	codes := make(map[string]int)
	for _, value := range responses {
		if value.Error == nil {
			t.Fatalf("重复请求意外成功：%+v", value)
		}
		codes[value.Error.Code]++
	}
	if len(responses) != 2 || codes["DUPLICATE_REQUEST"] != 1 || codes["CANCELED"] != 1 {
		t.Fatalf("重复编号处理=%+v", responses)
	}
}

func TestServeContinuesAfterMalformedRequest(t *testing.T) {
	run := startBridgePipe(t, &bridgeServiceStub{})
	if _, err := io.WriteString(run.input, "{\n"); err != nil {
		t.Fatal(err)
	}
	run.send(t, "valid", "status", nil)
	// 本测试验证坏消息之后仍可交互，先观察响应再关闭输入流。
	waitBridgeSignal(t, run.written, "无效请求响应写入")
	waitBridgeSignal(t, run.written, "后续有效请求响应写入")
	responses := run.finish(t)
	if len(responses) != 2 {
		t.Fatalf("响应数量=%d", len(responses))
	}
	first, second := responses[0], responses[1]
	if first.Error == nil || first.Error.Code != "INVALID_REQUEST" || second.ID != "valid" || second.Error != nil || second.Result == nil {
		t.Fatalf("无效请求中断了后续消息：first=%+v second=%+v", first, second)
	}
}

func TestDispatchRejectsMalformedArguments(t *testing.T) {
	server := &Server{Service: &bridgeServiceStub{}}
	for _, request := range []Request{
		{Method: "search", Args: json.RawMessage(`{"query":`)},
		bridgeRequest(t, "search", map[string]string{"query": " "}),
		bridgeRequest(t, "search", map[string]string{"query": strings.Repeat("剧", 101)}),
		bridgeRequest(t, "discover", map[string]string{"cursor": strings.Repeat("x", 16385)}),
		bridgeRequest(t, "discover", map[string]string{"channel": strings.Repeat("x", 101)}),
		bridgeRequest(t, "series", map[string]string{"id": "../../1001"}),
		bridgeRequest(t, "playback", map[string]string{"episode_id": "1001", "quality": strings.Repeat("x", 101)}),
		{Method: "unknown"},
	} {
		if _, err := server.Dispatch(context.Background(), request); !errors.Is(err, errInvalidInput) {
			t.Errorf("请求=%+v err=%v", request, err)
		}
	}
}
