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

type filterServiceStub struct {
	bridgeServiceStub
	panel  func(context.Context, string) ([]model.FilterRow, error)
	filter func(context.Context, sdk.ShortFilterQuery) (*model.ShortFilterPage, error)
}

func (s *filterServiceStub) ShortFilterPanel(ctx context.Context, genre string) ([]model.FilterRow, error) {
	return s.panel(ctx, genre)
}

func (s *filterServiceStub) ShortFilter(ctx context.Context, query sdk.ShortFilterQuery) (*model.ShortFilterPage, error) {
	return s.filter(ctx, query)
}

func TestFilterPreservesSelectionsOrderAndCursorWithoutWaitingForCovers(t *testing.T) {
	selections := map[string][]string{"genre": {"short_play"}, "category_dim_theme": {"cate_165"}, "sort": {"hot"}}
	want := &model.ShortFilterPage{
		Items:   []model.SeriesSummary{{ID: "1002", Title: "第二部"}, {ID: "1001", Title: "第一部"}},
		HasMore: true, Cursor: "next-cursor",
	}
	ctx := context.Background()
	service := &filterServiceStub{filter: func(received context.Context, query sdk.ShortFilterQuery) (*model.ShortFilterPage, error) {
		if received != ctx || !reflect.DeepEqual(query.Selections, selections) || query.Cursor != "previous-cursor" {
			t.Fatalf("筛选条件或续页上下文丢失：%+v", query)
		}
		return want, nil
	}}
	service.detail = func(context.Context, []string) ([]model.SeriesDetail, error) {
		t.Fatal("目录请求不应等待封面详情网络请求")
		return nil, nil
	}
	server := &Server{Service: service, details: map[string]cachedDetail{
		"1001": {Detail: model.SeriesDetail{Series: &model.Series{SeriesSummary: bridgeItem("1001")}}, At: time.Now()},
	}}
	result, err := server.Dispatch(ctx, bridgeRequest(t, "filter", map[string]any{"selections": selections, "cursor": "previous-cursor"}))
	if err != nil || result != want {
		t.Fatalf("筛选结果错误：%+v %v", result, err)
	}
	if !reflect.DeepEqual(bridgeIDs(want.Items), []string{"1002", "1001"}) || want.Cursor != "next-cursor" || !want.HasMore {
		t.Fatalf("筛选顺序或续页令牌被重写：%+v", want)
	}
	if want.Items[1].CoverURL == "" {
		t.Fatal("已缓存的封面未补入筛选结果")
	}
}

func TestFilterOptionsUsesActualServerRows(t *testing.T) {
	rows := []model.FilterRow{
		{Key: "category_dim_theme", Name: "题材", Options: []model.FilterOption{{Name: "重生", Value: "cate_165"}}},
		{Key: "category_dim_epoch", Name: "时代", Options: []model.FilterOption{{Name: "古代", Value: "ancient"}}},
	}
	server := &Server{Service: &filterServiceStub{panel: func(_ context.Context, genre string) ([]model.FilterRow, error) {
		if genre != "comic_series" {
			t.Fatalf("面板类型被替换：%s", genre)
		}
		return rows, nil
	}}}
	result, err := server.Dispatch(context.Background(), bridgeRequest(t, "filter_options", map[string]string{"genre": "comic_series"}))
	if err != nil || !reflect.DeepEqual(result, map[string]any{"rows": rows}) {
		t.Fatalf("真实筛选项未透传：%+v %v", result, err)
	}
}

func TestFilterRejectsInvalidInputsBeforeNetwork(t *testing.T) {
	server := &Server{Service: &filterServiceStub{
		panel: func(context.Context, string) ([]model.FilterRow, error) {
			t.Fatal("无效类型仍然请求面板")
			return nil, nil
		},
		filter: func(context.Context, sdk.ShortFilterQuery) (*model.ShortFilterPage, error) {
			t.Fatal("无效条件仍然请求内容")
			return nil, nil
		},
	}}
	if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "filter_options", map[string]string{"genre": "movie"})); !errors.Is(err, errInvalidInput) {
		t.Fatalf("无效类型未被拦截：%v", err)
	}
	for _, args := range []map[string]any{
		{"selections": map[string][]string{"unknown": {"one"}}},
		{"selections": map[string][]string{"genre": {"unknown"}}},
		{"selections": map[string][]string{"genre": {"short_play", "ai_series"}}},
		{"selections": map[string][]string{"category_dim_theme": {" "}}},
		{"selections": map[string][]string{"category_dim_theme": {strings.Repeat("x", 201)}}},
		{"selections": map[string][]string{"sort": make([]string, 21)}},
		{"cursor": strings.Repeat("x", 16385)},
	} {
		if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "filter", args)); !errors.Is(err, errInvalidInput) {
			t.Fatalf("无效筛选条件未被拦截：%+v %v", args, err)
		}
	}
}

func TestFilterPreservesUnavailableAndCancellation(t *testing.T) {
	for _, method := range []string{"filter_options", "filter"} {
		if _, err := (&Server{Service: &bridgeServiceStub{}}).Dispatch(context.Background(), bridgeRequest(t, method, nil)); !errors.Is(err, sdk.ErrContentUnavailable) {
			t.Fatalf("缺少 %s 能力时返回错误结果：%v", method, err)
		}
		for _, upstream := range []error{sdk.ErrTransport, sdk.ErrContentUnavailable, context.Canceled, context.DeadlineExceeded} {
			ctx, cancel := context.WithCancel(context.Background())
			if errors.Is(upstream, context.Canceled) {
				cancel()
			}
			service := &filterServiceStub{
				panel: func(received context.Context, _ string) ([]model.FilterRow, error) {
					if received != ctx {
						t.Fatal("面板取消上下文丢失")
					}
					return nil, upstream
				},
				filter: func(received context.Context, _ sdk.ShortFilterQuery) (*model.ShortFilterPage, error) {
					if received != ctx {
						t.Fatal("筛选取消上下文丢失")
					}
					return nil, upstream
				},
			}
			_, err := (&Server{Service: service}).Dispatch(ctx, bridgeRequest(t, method, nil))
			cancel()
			if !errors.Is(err, upstream) {
				t.Fatalf("%s 错误传播异常：%v", method, err)
			}
		}
	}
}

func TestEngagementUsesCorrectSeriesAndEpisodeWithoutInventingMissingCounts(t *testing.T) {
	server := &Server{Service: &bridgeServiceStub{detail: func(_ context.Context, ids []string) ([]model.SeriesDetail, error) {
		if !reflect.DeepEqual(ids, []string{"1001"}) {
			t.Fatalf("统计请求用了分集或其他剧的 ID：%v", ids)
		}
		return []model.SeriesDetail{{
			Series: &model.Series{SeriesSummary: bridgeItem("1001"), PlayCount: 30000, FollowCount: 400, DiggCount: 90},
			Episodes: []model.Episode{
				{ID: "2001", SeriesID: "1001", Number: 1, CommentCount: 12, DiggCount: 34},
				{ID: "2002", SeriesID: "1001", Number: 2},
			},
		}}, nil
	}}}
	for _, episodeID := range []string{"2001", "2002", ""} {
		result, err := server.Dispatch(context.Background(), bridgeRequest(t, "engagement", map[string]string{"series_id": "1001", "episode_id": episodeID}))
		if err != nil {
			t.Fatal(err)
		}
		stats := result.(engagement)
		if stats.SeriesID != "1001" || stats.EpisodeID != episodeID || stats.PlayCount == nil || *stats.PlayCount != 30000 || stats.FollowCount == nil || *stats.FollowCount != 400 || stats.SeriesLikeCount == nil || *stats.SeriesLikeCount != 90 {
			t.Fatalf("剧集统计映射错误：%+v", stats)
		}
		if episodeID == "2001" {
			if stats.CommentCount == nil || *stats.CommentCount != 12 || stats.EpisodeLikeCount == nil || *stats.EpisodeLikeCount != 34 {
				t.Fatalf("分集统计未使用所选分集：%+v", stats)
			}
		} else if stats.CommentCount != nil || stats.EpisodeLikeCount != nil {
			t.Fatalf("缺失统计被伪造成零或拿到了另一集的数据：%+v", stats)
		}
		raw, err := json.Marshal(stats)
		if err != nil {
			t.Fatal(err)
		}
		var values map[string]any
		_ = json.Unmarshal(raw, &values)
		if values["favorite_count"] != nil || values["comments_available"] != false || values["danmaku_available"] != false || stats.Note == "" {
			t.Fatalf("未开放的互动能力被伪造：%s", raw)
		}
	}
	if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "engagement", map[string]string{"series_id": "1001", "episode_id": "9999"})); !errors.Is(err, sdk.ErrContentUnavailable) {
		t.Fatalf("不属于这部剧的分集仍获得统计：%v", err)
	}
}

func TestEngagementRejectsInvalidIDsAndPreservesServiceErrors(t *testing.T) {
	for _, args := range []map[string]string{{"series_id": "bad"}, {"series_id": "1001", "episode_id": "bad"}} {
		server := &Server{Service: &bridgeServiceStub{detail: func(context.Context, []string) ([]model.SeriesDetail, error) {
			t.Fatal("无效 ID 仍然请求互动统计")
			return nil, nil
		}}}
		if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "engagement", args)); !errors.Is(err, errInvalidInput) {
			t.Fatalf("无效 ID 未被拦截：%v", err)
		}
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	server := &Server{Service: &bridgeServiceStub{detail: func(received context.Context, _ []string) ([]model.SeriesDetail, error) {
		return nil, received.Err()
	}}}
	if _, err := server.Dispatch(ctx, bridgeRequest(t, "engagement", map[string]string{"series_id": "1001"})); !errors.Is(err, context.Canceled) {
		t.Fatalf("统计请求取消语义丢失：%v", err)
	}
}
