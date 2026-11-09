package nativebridge

import (
	"context"
	"encoding/json"
	"errors"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

func TestCatalogReturnsCurrentPageWithoutWaitingForPostersOrMorePages(t *testing.T) {
	for _, method := range []string{"discover", "search"} {
		t.Run(method, func(t *testing.T) {
			calls := 0
			page := &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{{ID: "1001", Title: "立即展示"}}, HasMore: true, Cursor: "next"}
			server := &Server{Service: &bridgeServiceStub{
				discover: func(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
					calls++
					return page, nil
				},
				search: func(context.Context, string, sdk.SearchOptions) (*sdk.Page[model.SeriesSummary], error) {
					calls++
					return page, nil
				},
				detail: func(ctx context.Context, _ []string) ([]model.SeriesDetail, error) {
					<-ctx.Done()
					return nil, ctx.Err()
				},
			}}
			ctx, cancel := context.WithTimeout(context.Background(), 200*time.Millisecond)
			defer cancel()
			started := time.Now()
			result, err := server.Dispatch(ctx, bridgeRequest(t, method, map[string]string{"query": "全球死考"}))
			if err != nil || time.Since(started) >= 150*time.Millisecond || calls != 1 {
				t.Fatalf("目录等待了不相关请求：calls=%d elapsed=%v err=%v", calls, time.Since(started), err)
			}
			var got *sdk.Page[model.SeriesSummary]
			if method == "discover" {
				catalog := result.(*recommendationPage)
				got = &catalog.Page
				var next recommendationCursor
				if json.Unmarshal([]byte(strings.TrimPrefix(got.Cursor, recommendationCursorPrefix)), &next) != nil || next.Cursor != "next" || next.Source != "discover" {
					t.Fatalf("发现来源的续页位置丢失：%+v", catalog)
				}
				got.Cursor = next.Cursor
			} else {
				got = result.(*sdk.Page[model.SeriesSummary])
			}
			if len(got.Items) != 1 || got.Cursor != "next" || !got.HasMore {
				t.Fatalf("目录或游标丢失：%+v", got)
			}
		})
	}
}

func TestSearchCategoriesKeepSDKResultsAndContinuation(t *testing.T) {
	for _, tab := range []string{"综合", "剧集", "视频"} {
		server := &Server{Service: &bridgeServiceStub{search: func(_ context.Context, query string, options sdk.SearchOptions) (*sdk.Page[model.SeriesSummary], error) {
			if query != "全球死考" || options.Tab != tab || options.Cursor != "20" {
				t.Errorf("检索参数改变：%q %+v", query, options)
			}
			return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{{ID: "1001", Title: "保留服务端原始标题"}, {ID: "1002", Title: "另一结果"}}, HasMore: true, Cursor: "40"}, nil
		}}}
		result, err := server.Dispatch(context.Background(), bridgeRequest(t, "search", map[string]string{"query": "全球死考", "tab": tab, "cursor": "20"}))
		if err != nil {
			t.Fatal(err)
		}
		page := result.(*sdk.Page[model.SeriesSummary])
		if !reflect.DeepEqual(bridgeIDs(page.Items), []string{"1001", "1002"}) || page.Cursor != "40" || !page.HasMore {
			t.Fatalf("检索结果或分页丢失：%+v", page)
		}
	}
}

func TestHydrateReturnsRealPostersAndReusesDetailCache(t *testing.T) {
	calls := 0
	server := &Server{Service: &bridgeServiceStub{detail: func(_ context.Context, ids []string) ([]model.SeriesDetail, error) {
		calls++
		if !reflect.DeepEqual(ids, []string{"1001", "1002"}) {
			t.Fatalf("补图未按ID去重：%v", ids)
		}
		return []model.SeriesDetail{
			{Series: &model.Series{SeriesSummary: model.SeriesSummary{ID: "1001", Title: "真正海报", CoverURL: "https://images.invalid/poster.heic?signature=original"}}, Episodes: []model.Episode{{CoverURL: "https://images.invalid/frame.jpg"}}},
			{Series: &model.Series{SeriesSummary: model.SeriesSummary{ID: "1002", Title: "另一海报", CoverURL: "https://images.invalid/poster2.heic"}}},
		}, nil
	}}}
	for i := 0; i < 2; i++ {
		result, err := server.Dispatch(context.Background(), bridgeRequest(t, "hydrate", map[string]any{"ids": []string{"1001", "1002", "1001"}}))
		if err != nil {
			t.Fatal(err)
		}
		items := result.(map[string]any)["items"].([]model.SeriesSummary)
		if len(items) != 2 || items[0].Title != "真正海报" || items[0].CoverURL != "https://images.invalid/poster.heic?signature=original" {
			t.Fatalf("未返回真正系列海报：%+v", items)
		}
	}
	if calls != 1 {
		t.Fatalf("补图缓存失效：calls=%d", calls)
	}
	for _, ids := range [][]string{{"../bad"}, make([]string, 101)} {
		if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "hydrate", map[string]any{"ids": ids})); !errors.Is(err, errInvalidInput) {
			t.Fatalf("无效补图参数未拒绝：%v", err)
		}
	}
}

func TestPlaybackQualityChoicesMatchSDKAndReuseUnexpiredModel(t *testing.T) {
	calls := 0
	pb := &model.Playback{EpisodeID: "2001", DurationSeconds: 88, DurationMS: 88400, ExpiresAt: time.Now().Add(time.Hour).Unix(), Streams: []model.Stream{
		{Definition: "720p", Width: 1280, Height: 720, Bitrate: 2000, URL: "https://media.invalid/720-high"},
		{Definition: "2160p", Width: 3840, Height: 2160, Bitrate: 8000, URL: "https://media.invalid/2160"},
		{Definition: "720p", Width: 1280, Height: 720, Bitrate: 1000, URL: "https://media.invalid/720-low"},
		{Definition: "bad", Height: 9000},
	}}
	server := &Server{Service: &bridgeServiceStub{playback: func(context.Context, string, sdk.PlaybackOptions) (*model.Playback, error) { calls++; return pb, nil }}}
	for _, quality := range []string{"best", "720p", "2160p"} {
		result, err := server.Dispatch(context.Background(), bridgeRequest(t, "playback", map[string]string{"episode_id": "2001", "quality": quality}))
		if err != nil {
			t.Fatal(err)
		}
		value := result.(map[string]any)
		if value["expires_at"] != pb.ExpiresAt {
			t.Fatalf("预准备所需有效期未透传：%v", value["expires_at"])
		}
		if value["duration_seconds"] != 88 || value["duration_ms"] != int64(88400) {
			t.Fatalf("时长精度或兼容字段丢失：seconds=%v milliseconds=%v", value["duration_seconds"], value["duration_ms"])
		}
		stream, _ := pb.Select(quality)
		if value["url"] != stream.URL || value["definition"] != stream.Definition {
			t.Fatalf("画质选择与SDK不一致：%+v", value)
		}
		if value["width"] != stream.Width || value["height"] != stream.Height || value["bitrate"] != stream.Bitrate {
			t.Fatalf("当前码流元数据未完整透传：width=%v height=%v bitrate=%v", value["width"], value["height"], value["bitrate"])
		}
		choices := value["available_qualities"].([]playbackQuality)
		if len(choices) != 2 || choices[0].Definition != "720p" || choices[0].Bitrate != 2000 {
			t.Fatalf("选项元数据与真实选择不一致：%+v", choices)
		}
		for _, choice := range choices {
			if strings.Contains(choice.Definition, "https:") {
				t.Fatal("画质列表包含地址")
			}
		}
	}
	if calls != 1 {
		t.Fatalf("切画质重复请求了未过期播放模型：%d", calls)
	}
	pb.ExpiresAt = time.Now().Unix() - 1
	if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "playback", map[string]string{"episode_id": "2001", "quality": "best"})); err != nil || calls != 2 {
		t.Fatalf("过期模型未刷新：calls=%d err=%v", calls, err)
	}
	if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "playback", map[string]string{"episode_id": "2001", "quality": "not-offered"})); !errors.Is(err, errQualityUnavailable) {
		t.Fatalf("不存在画质被伪装为成功：%v", err)
	}
}
