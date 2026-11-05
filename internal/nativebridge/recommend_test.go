package nativebridge

import (
	"context"
	"encoding/json"
	"errors"
	"reflect"
	"testing"

	"github.com/beilunyang/openhongguo/internal/protocol"
	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

func TestCatalogEmptyRecommendationUsesGenuineFilterAndOwnContinuation(t *testing.T) {
	for _, method := range []string{"discover", "recommend"} {
		t.Run(method, func(t *testing.T) {
			discoverCalls, filterCalls := 0, 0
			service := &filterServiceStub{filter: func(_ context.Context, query sdk.ShortFilterQuery) (*model.ShortFilterPage, error) {
				filterCalls++
				if !reflect.DeepEqual(query.Selections, map[string][]string{"sort": {"hot_score"}}) {
					t.Fatalf("热度目录条件被改变：%+v", query.Selections)
				}
				if filterCalls == 1 && query.Cursor != "" || filterCalls == 2 && query.Cursor != "filter-2" {
					t.Fatalf("来源续页令牌不正确：%+v", query)
				}
				return &model.ShortFilterPage{Items: []model.SeriesSummary{bridgeItem("1001"), bridgeItem("1002"), bridgeItem("1002"), bridgeItem("bad")}, Cursor: "filter-2", HasMore: true}, nil
			}}
			service.discover = func(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
				discoverCalls++
				return nil, sdk.ErrAnonymousLimited
			}
			server := &Server{Service: service}
			result, err := server.Dispatch(context.Background(), bridgeRequest(t, method, map[string]string{"series_id": "1001"}))
			if err != nil {
				t.Fatal(err)
			}
			page := result.(*recommendationPage)
			wantIDs := []string{"1002"}
			if method == "discover" {
				wantIDs = []string{"1001", "1002"}
			}
			if page.Source != "filter" || page.SourceLabel == "" || page.FallbackReason != "discover_empty" || !page.HasMore || !reflect.DeepEqual(bridgeIDs(page.Items), wantIDs) {
				t.Fatalf("真实来源或条目被误报：%+v", page)
			}
			raw, _ := json.Marshal(page)
			var wire map[string]json.RawMessage
			if json.Unmarshal(raw, &wire) != nil || wire["items"] == nil || wire["source"] == nil || wire["has_more"] == nil {
				t.Fatalf("目录协议不兼容：%s", raw)
			}
			result, err = server.Dispatch(context.Background(), bridgeRequest(t, method, map[string]string{"series_id": "1001", "cursor": page.Cursor}))
			if err != nil || discoverCalls != 1 || filterCalls != 2 || result.(*recommendationPage).HasMore {
				t.Fatalf("续页错误重查发现页或游标未前进：%+v %v calls=%d/%d", result, err, discoverCalls, filterCalls)
			}
		})
	}
}

func TestCatalogPreservesProtectedErrorsAtEverySource(t *testing.T) {
	protected := []error{context.Canceled, context.DeadlineExceeded, sdk.ErrRateLimited, sdk.ErrAuthenticationRequired, sdk.ErrChallengeRequired, sdk.ErrDeviceRejected, sdk.ErrSignatureRejected, sdk.ErrTransport}
	for _, source := range []string{"discover", "filter", "related"} {
		for _, upstream := range protected {
			t.Run(source+"/"+upstream.Error(), func(t *testing.T) {
				calls := 0
				service := &filterServiceStub{filter: func(context.Context, sdk.ShortFilterQuery) (*model.ShortFilterPage, error) {
					calls++
					if source == "discover" {
						t.Fatal("发现接口的明确错误仍被换来源")
					}
					return nil, upstream
				}}
				service.discover = func(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
					if source == "discover" {
						return nil, upstream
					}
					if source == "related" {
						return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{bridgeItem("1002")}}, nil
					}
					return nil, sdk.ErrAnonymousLimited
				}
				service.recommend = func(context.Context, string) (*model.Recommend, error) { return nil, upstream }
				_, err := (&Server{Service: service}).Dispatch(context.Background(), bridgeRequest(t, "recommend", map[string]string{"series_id": "1001"}))
				if !errors.Is(err, upstream) {
					t.Fatalf("明确错误被吞掉：%v", err)
				}
				if source == "filter" && calls != 1 {
					t.Fatalf("目录请求次数=%d", calls)
				}
			})
		}
	}
	if failure := explainError(sdk.ErrDeviceRejected); failure.Code != "DEVICE_REJECTED" {
		t.Fatalf("设备拒绝与空列表仍使用同一错误码：%+v", failure)
	}
}

func TestCatalogBindsChannelMethodAndContentType(t *testing.T) {
	for _, test := range []struct{ channel, genre string }{{"真人剧", "short_play"}, {"漫剧", "comic_series"}} {
		service := &filterServiceStub{filter: func(_ context.Context, q sdk.ShortFilterQuery) (*model.ShortFilterPage, error) {
			if !reflect.DeepEqual(q.Selections["genre"], []string{test.genre}) {
				t.Fatalf("频道类型被混用：%+v", q)
			}
			return &model.ShortFilterPage{Items: []model.SeriesSummary{bridgeItem("1002")}, Cursor: "next", HasMore: true}, nil
		}}
		service.discover = func(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
			return nil, sdk.ErrAnonymousLimited
		}
		server := &Server{Service: service}
		first, err := server.Dispatch(context.Background(), bridgeRequest(t, "recommend", map[string]string{"channel": test.channel}))
		if err != nil {
			t.Fatal(err)
		}
		cursor := first.(*recommendationPage).Cursor
		for _, args := range []map[string]string{{"channel": "推荐", "cursor": cursor}, {"channel": test.channel, "cursor": "not-a-source-cursor"}} {
			if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "recommend", args)); !errors.Is(err, errInvalidInput) {
				t.Fatalf("错配游标未被拦截：%+v %v", args, err)
			}
		}
		if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "discover", map[string]string{"channel": test.channel, "cursor": cursor})); !errors.Is(err, errInvalidInput) {
			t.Fatalf("跨方法游标未被拦截：%v", err)
		}
	}
	service := &filterServiceStub{filter: func(context.Context, sdk.ShortFilterQuery) (*model.ShortFilterPage, error) {
		t.Fatal("电影频道被短剧目录替代")
		return nil, nil
	}}
	service.discover = func(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
		return nil, sdk.ErrAnonymousLimited
	}
	if _, err := (&Server{Service: service}).Dispatch(context.Background(), bridgeRequest(t, "discover", map[string]string{"channel": "电影"})); !errors.Is(err, sdk.ErrAnonymousLimited) {
		t.Fatalf("不可替代的频道被误报成功：%v", err)
	}
}

func TestCatalogNormalEndDoesNotRestartFallback(t *testing.T) {
	for _, upstream := range []error{nil, sdk.ErrEndOfList} {
		service := &filterServiceStub{filter: func(context.Context, sdk.ShortFilterQuery) (*model.ShortFilterPage, error) {
			t.Fatal("正常末页重新启动了热度目录")
			return nil, nil
		}}
		service.discover = func(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
			return &sdk.Page[model.SeriesSummary]{}, upstream
		}
		result, err := (&Server{Service: service}).Dispatch(context.Background(), bridgeRequest(t, "recommend", nil))
		if err != nil || result.(*recommendationPage).HasMore || len(result.(*recommendationPage).Items) != 0 {
			t.Fatalf("正常结束未被保留：%+v %v", result, err)
		}
	}
}

func TestCatalogTypedChannelsDoNotMergeGenericRelated(t *testing.T) {
	for _, channel := range []string{"真人剧", "漫剧", "电影"} {
		service := &bridgeServiceStub{
			discover: func(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
				return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{bridgeItem("1002")}}, nil
			},
			recommend: func(context.Context, string) (*model.Recommend, error) {
				t.Fatal("类型频道混入了泛推荐列表")
				return nil, nil
			},
		}
		result, err := (&Server{Service: service}).Dispatch(context.Background(), bridgeRequest(t, "recommend", map[string]string{"series_id": "1001", "channel": channel}))
		if err != nil || !reflect.DeepEqual(bridgeIDs(result.(*recommendationPage).Items), []string{"1002"}) {
			t.Fatalf("类型频道结果被替换：%+v %v", result, err)
		}
	}
}

func TestCatalogCancellationStopsBeforeFallbackAndEmptyMessageIsSpecific(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	service := &filterServiceStub{filter: func(context.Context, sdk.ShortFilterQuery) (*model.ShortFilterPage, error) {
		t.Fatal("取消后仍然开始备用目录请求")
		return nil, nil
	}}
	service.discover = func(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
		cancel()
		return nil, sdk.ErrAnonymousLimited
	}
	if _, err := (&Server{Service: service}).Dispatch(ctx, bridgeRequest(t, "recommend", nil)); !errors.Is(err, context.Canceled) {
		t.Fatalf("取消被吞掉：%v", err)
	}
	empty := &Server{Service: &bridgeServiceStub{discover: func(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
		return nil, sdk.ErrAnonymousLimited
	}}}
	_, err := empty.Dispatch(context.Background(), bridgeRequest(t, "recommend", nil))
	if !errors.Is(err, errRecommendationEmpty) || explainError(err).Message != "推荐列表与热门目录暂未返回内容，请稍后重试" {
		t.Fatalf("所有真实来源为空时提示仍然不明确：%v", err)
	}
}

func TestCatalogKeepsLegacyDiscoverCursorOnOriginalSource(t *testing.T) {
	legacy, err := protocol.EncodeFeedCursor(protocol.FeedContinuation{TabType: "16", CellID: "1001", Offset: "9", SessionID: "test", ShowType: "401"})
	if err != nil {
		t.Fatal(err)
	}
	service := &bridgeServiceStub{discover: func(_ context.Context, q sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
		if q.Cursor != legacy || q.Channel != "推荐" {
			t.Fatalf("老发现游标被改写：%+v", q)
		}
		return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{bridgeItem("1002")}}, nil
	}}
	if _, err := (&Server{Service: service}).Dispatch(context.Background(), bridgeRequest(t, "discover", map[string]string{"cursor": legacy})); err != nil {
		t.Fatal(err)
	}
}
