package nativebridge

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"reflect"
	"strings"
	"testing"

	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

type discussionServiceStub struct {
	bridgeServiceStub
	comments func(context.Context, string, string) (*model.DiscussionPage, error)
	danmaku  func(context.Context, string, string, sdk.DanmakuOptions) (*model.DiscussionPage, error)
}

func (s *discussionServiceStub) WebEpisodeComments(ctx context.Context, seriesID, episodeID string) (*model.DiscussionPage, error) {
	return s.comments(ctx, seriesID, episodeID)
}

func (s *discussionServiceStub) Danmaku(ctx context.Context, seriesID, episodeID string, options sdk.DanmakuOptions) (*model.DiscussionPage, error) {
	return s.danmaku(ctx, seriesID, episodeID, options)
}

func TestDiscussionPreservesSelectedEpisodeContextAndPage(t *testing.T) {
	for _, method := range []string{"comments", "danmaku"} {
		t.Run(method, func(t *testing.T) {
			ctx := context.Background()
			total, zero, position := int64(1300), int64(0), int64(2000)
			want := &model.DiscussionPage{
				Items: []model.DiscussionItem{{ID: "c-1", Text: "真实正文", Author: "作者", AvatarURL: "https://images.invalid/avatar.png", LikeCount: &zero, CreatedAt: 1700000000, PositionMS: &position, Color: "#ffffff"}},
				Total: &total, Cursor: "60000", HasMore: true, SourceLimited: true, Source: "upstream", Note: "来源只开放部分内容",
			}
			originalJSON, err := json.Marshal(want)
			if err != nil {
				t.Fatal(err)
			}
			calls := 0
			check := func(received context.Context, seriesID, episodeID string) {
				t.Helper()
				calls++
				if received != ctx || seriesID != "1001" || episodeID != "2001" {
					t.Fatalf("所选分集或上下文丢失：%s %s", seriesID, episodeID)
				}
			}
			service := &discussionServiceStub{
				comments: func(received context.Context, seriesID, episodeID string) (*model.DiscussionPage, error) {
					if method != "comments" {
						t.Fatal("弹幕请求调用了评论接口")
					}
					check(received, seriesID, episodeID)
					return want, nil
				},
				danmaku: func(received context.Context, seriesID, episodeID string, options sdk.DanmakuOptions) (*model.DiscussionPage, error) {
					if method != "danmaku" {
						t.Fatal("评论请求调用了弹幕接口")
					}
					check(received, seriesID, episodeID)
					if options.Cursor != "0002" || options.DurationMS != 90000 {
						t.Fatalf("弹幕游标或时长未透传：%+v", options)
					}
					return want, nil
				},
			}
			args := map[string]any{"series_id": "1001", "episode_id": "2001"}
			if method == "danmaku" {
				args["cursor"], args["duration_ms"] = "0002", 90000
			}
			result, err := (&Server{Service: service}).Dispatch(ctx, bridgeRequest(t, method, args))
			if err != nil || result != want || calls != 1 {
				t.Fatalf("互动正文或来源元数据被改写：result=%+v calls=%d err=%v", result, calls, err)
			}
			resultJSON, err := json.Marshal(result)
			if err != nil || string(resultJSON) != string(originalJSON) {
				t.Fatalf("互动正文或来源字段被修改：%s err=%v", resultJSON, err)
			}
		})
	}
}

func TestDanmakuAcceptsBoundaryDurationAndNumericCursor(t *testing.T) {
	for _, tt := range []struct {
		durationMS int64
		cursor     string
	}{
		{durationMS: 1},
		{durationMS: 1, cursor: "0"},
		{durationMS: 1, cursor: strings.Repeat("0", 19)},
		{durationMS: 86400000, cursor: "86399999"},
		{durationMS: 90000, cursor: "0002"},
	} {
		service := &discussionServiceStub{danmaku: func(_ context.Context, _, _ string, options sdk.DanmakuOptions) (*model.DiscussionPage, error) {
			if options.DurationMS != tt.durationMS || options.Cursor != tt.cursor {
				t.Fatalf("有效边界被修改：%+v", options)
			}
			return &model.DiscussionPage{}, nil
		}}
		_, err := (&Server{Service: service}).Dispatch(context.Background(), bridgeRequest(t, "danmaku", map[string]any{"series_id": "1001", "episode_id": "2001", "duration_ms": tt.durationMS, "cursor": tt.cursor}))
		if err != nil {
			t.Fatalf("有效弹幕边界被拦截：%+v %v", tt, err)
		}
	}
}

func TestDiscussionNormalizesEmptyItemsWithoutInventingTotal(t *testing.T) {
	for _, method := range []string{"comments", "danmaku"} {
		for _, nilPage := range []bool{false, true} {
			t.Run(fmt.Sprintf("%s/nil_page_%t", method, nilPage), func(t *testing.T) {
				var page *model.DiscussionPage
				if !nilPage {
					page = &model.DiscussionPage{Source: "website", SourceLimited: true, Note: "暂无预览正文"}
				}
				service := &discussionServiceStub{
					comments: func(context.Context, string, string) (*model.DiscussionPage, error) { return page, nil },
					danmaku: func(context.Context, string, string, sdk.DanmakuOptions) (*model.DiscussionPage, error) {
						return page, nil
					},
				}
				args := map[string]any{"series_id": "1001", "episode_id": "2001"}
				if method == "danmaku" {
					args["duration_ms"] = 90000
				}
				result, err := (&Server{Service: service}).Dispatch(context.Background(), bridgeRequest(t, method, args))
				if nilPage {
					if !errors.Is(err, sdk.ErrContentUnavailable) {
						t.Fatalf("缺失响应被当作已加载空列表：%+v %v", result, err)
					}
					return
				}
				if err != nil {
					t.Fatal(err)
				}
				got := result.(*model.DiscussionPage)
				if got.Items == nil || len(got.Items) != 0 || got.Total != nil {
					t.Fatalf("空结果被伪造计数：%+v", got)
				}
				if !nilPage && (!got.SourceLimited || got.Source != "website" || got.Note != "暂无预览正文") {
					t.Fatalf("空预览的来源限制丢失：%+v", got)
				}
				raw, err := json.Marshal(got)
				if err != nil || !strings.Contains(string(raw), `"items":[]`) || !strings.Contains(string(raw), `"total":null`) {
					t.Fatalf("空数组或未知总数协议错误：%s %v", raw, err)
				}
			})
		}
	}
}

func TestDiscussionRejectsInvalidArgumentsBeforeService(t *testing.T) {
	service := &discussionServiceStub{
		comments: func(context.Context, string, string) (*model.DiscussionPage, error) {
			t.Fatal("无效参数仍调用了评论接口")
			return nil, nil
		},
		danmaku: func(context.Context, string, string, sdk.DanmakuOptions) (*model.DiscussionPage, error) {
			t.Fatal("无效参数仍调用了弹幕接口")
			return nil, nil
		},
	}
	for _, method := range []string{"comments", "danmaku"} {
		invalid := []map[string]any{
			{"series_id": ""}, {"series_id": "../bad"}, {"episode_id": ""}, {"episode_id": "bad"},
			{"cursor": strings.Repeat("1", 16385)},
		}
		if method == "comments" {
			invalid = append(invalid, map[string]any{"cursor": "1"}, map[string]any{"duration_ms": 1})
		} else {
			invalid = append(invalid,
				map[string]any{"duration_ms": 0}, map[string]any{"duration_ms": -1}, map[string]any{"duration_ms": 86400001},
				map[string]any{"duration_ms": 1.5}, map[string]any{"duration_ms": "90000"},
				map[string]any{"cursor": "-1"}, map[string]any{"cursor": "+1"}, map[string]any{"cursor": "1.5"},
				map[string]any{"cursor": " 1"}, map[string]any{"cursor": "abc"}, map[string]any{"cursor": "90000"},
				map[string]any{"cursor": "90001"}, map[string]any{"cursor": "999999999999999999999999"},
				map[string]any{"cursor": strings.Repeat("0", 20)},
			)
		}
		for i, change := range invalid {
			t.Run(fmt.Sprintf("%s/%d", method, i), func(t *testing.T) {
				args := map[string]any{"series_id": "1001", "episode_id": "2001"}
				if method == "danmaku" {
					args["duration_ms"] = 90000
				}
				for key, value := range change {
					args[key] = value
				}
				if _, err := (&Server{Service: service}).Dispatch(context.Background(), bridgeRequest(t, method, args)); !errors.Is(err, errInvalidInput) {
					t.Fatalf("无效参数未被拦截：%+v err=%v", change, err)
				}
			})
		}
	}
}

func TestDiscussionPreservesUnavailableErrorsAndCancellation(t *testing.T) {
	for _, method := range []string{"comments", "danmaku"} {
		args := map[string]any{"series_id": "1001", "episode_id": "2001"}
		if method == "danmaku" {
			args["duration_ms"] = 90000
		}
		request := bridgeRequest(t, method, args)
		if _, err := (&Server{Service: &bridgeServiceStub{}}).Dispatch(context.Background(), request); !errors.Is(err, sdk.ErrContentUnavailable) {
			t.Fatalf("缺少 %s 接口时返回错误结果：%v", method, err)
		}
		for _, upstream := range []error{sdk.ErrTransport, sdk.ErrContentUnavailable, sdk.ErrAnonymousLimited, sdk.ErrRateLimited, sdk.ErrAuthenticationRequired, context.Canceled, context.DeadlineExceeded} {
			service := &discussionServiceStub{
				comments: func(context.Context, string, string) (*model.DiscussionPage, error) {
					return nil, fmt.Errorf("source: %w", upstream)
				},
				danmaku: func(context.Context, string, string, sdk.DanmakuOptions) (*model.DiscussionPage, error) {
					return nil, fmt.Errorf("source: %w", upstream)
				},
			}
			if result, err := (&Server{Service: service}).Dispatch(context.Background(), request); !errors.Is(err, upstream) || result != (*model.DiscussionPage)(nil) {
				t.Fatalf("%s 失败被伪装为空结果：%+v %v", method, result, err)
			}
		}
		ctx, cancel := context.WithCancel(context.Background())
		service := &discussionServiceStub{
			comments: func(received context.Context, _, _ string) (*model.DiscussionPage, error) {
				if received != ctx {
					t.Fatal("评论请求取消上下文丢失")
				}
				cancel()
				return &model.DiscussionPage{Items: []model.DiscussionItem{{Text: "过期结果"}}}, nil
			},
			danmaku: func(received context.Context, _, _ string, _ sdk.DanmakuOptions) (*model.DiscussionPage, error) {
				if received != ctx {
					t.Fatal("弹幕请求取消上下文丢失")
				}
				cancel()
				return &model.DiscussionPage{Items: []model.DiscussionItem{{Text: "过期结果"}}}, nil
			},
		}
		if _, err := (&Server{Service: service}).Dispatch(ctx, request); !errors.Is(err, context.Canceled) {
			t.Fatalf("%s 请求取消后仍提交正文：%v", method, err)
		}
		service.comments = func(context.Context, string, string) (*model.DiscussionPage, error) {
			t.Fatal("已取消请求仍加载评论")
			return nil, nil
		}
		service.danmaku = func(context.Context, string, string, sdk.DanmakuOptions) (*model.DiscussionPage, error) {
			t.Fatal("已取消请求仍加载弹幕")
			return nil, nil
		}
		if _, err := (&Server{Service: service}).Dispatch(ctx, request); !errors.Is(err, context.Canceled) {
			t.Fatalf("%s 已取消请求丢失取消状态：%v", method, err)
		}
	}
}

func TestEngagementSupportDoesNotLoadDiscussionOrInventMissingCounts(t *testing.T) {
	service := &discussionServiceStub{
		bridgeServiceStub: bridgeServiceStub{detail: func(context.Context, []string) ([]model.SeriesDetail, error) {
			return []model.SeriesDetail{{Series: &model.Series{SeriesSummary: bridgeItem("1001")}, Episodes: []model.Episode{{ID: "2001", SeriesID: "1001"}}}}, nil
		}},
		comments: func(context.Context, string, string) (*model.DiscussionPage, error) {
			t.Fatal("统计请求等待评论正文")
			return nil, nil
		},
		danmaku: func(context.Context, string, string, sdk.DanmakuOptions) (*model.DiscussionPage, error) {
			t.Fatal("统计请求等待弹幕正文")
			return nil, nil
		},
	}
	commentsOnly := &struct {
		Service
		episodeCommentsService
	}{&service.bridgeServiceStub, service}
	danmakuOnly := &struct {
		Service
		danmakuService
	}{&service.bridgeServiceStub, service}
	for _, tt := range []struct {
		name     string
		service  Service
		comments bool
		danmaku  bool
	}{
		{name: "neither", service: &service.bridgeServiceStub},
		{name: "comments_only", service: commentsOnly, comments: true},
		{name: "danmaku_only", service: danmakuOnly, danmaku: true},
		{name: "both", service: service, comments: true, danmaku: true},
	} {
		t.Run(tt.name, func(t *testing.T) {
			server := &Server{Service: tt.service}
			for _, episodeID := range []string{"", "2001"} {
				result, err := server.Dispatch(context.Background(), bridgeRequest(t, "engagement", map[string]string{"series_id": "1001", "episode_id": episodeID}))
				if err != nil {
					t.Fatal(err)
				}
				stats := result.(engagement)
				if stats.CommentsAvailable != (episodeID != "" && tt.comments) || stats.DanmakuAvailable != (episodeID != "" && tt.danmaku) {
					t.Fatalf("分集互动接口支持旗标错误：%+v", stats)
				}
				if !reflect.DeepEqual([]*int64{stats.CommentCount, stats.FavoriteCount, stats.EpisodeLikeCount}, []*int64{nil, nil, nil}) {
					t.Fatalf("接口存在被伪造成已有统计：%+v", stats)
				}
			}
		})
	}
}
