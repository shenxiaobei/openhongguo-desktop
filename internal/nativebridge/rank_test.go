package nativebridge

import (
	"context"
	"encoding/json"
	"errors"
	"reflect"
	"strings"
	"testing"

	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

type rankServiceStub struct {
	bridgeServiceStub
	rank func(context.Context, sdk.ShortRankQuery) (*model.ShortRanking, error)
}

func (s *rankServiceStub) ShortRank(ctx context.Context, query sdk.ShortRankQuery) (*model.ShortRanking, error) {
	return s.rank(ctx, query)
}

func TestRankPreservesCatalogOrderAndCursor(t *testing.T) {
	want := &model.ShortRanking{
		Contents: []model.RankContent{{ID: "human", Name: "真人剧", Selected: true, Boards: []model.RankBoard{
			{ID: "human_hot_rank", Name: "热播榜", Selected: true},
			{ID: "human_new_rank", Name: "新剧榜"},
		}}},
		Content: "human", Board: "human_hot_rank", Version: "fixture-version",
		Items: []model.RankItem{
			{SeriesID: "1001", Title: "第一部", Rank: 1, Position: 21, Score: "100万热度", EpisodeCount: 60},
			{SeriesID: "1002", Title: "平台推荐作品", Rank: 2, Position: 22, Score: "平台推荐", Promoted: true},
			{SeriesID: "1003", Title: "下一部", Rank: 2, Position: 23, Score: "99万热度"},
		},
		HasMore: true, Cursor: "next-ranking-cursor",
	}
	query := sdk.ShortRankQuery{Content: "human", Board: "human_hot_rank", Cursor: "previous-ranking-cursor"}
	server := &Server{Service: &rankServiceStub{rank: func(ctx context.Context, got sdk.ShortRankQuery) (*model.ShortRanking, error) {
		if ctx == nil || !reflect.DeepEqual(got, query) {
			t.Fatalf("榜单查询参数未原样传递：%+v", got)
		}
		return want, nil
	}}}
	result, err := server.Dispatch(context.Background(), bridgeRequest(t, "rank", map[string]string{
		"content": query.Content, "board": query.Board, "cursor": query.Cursor,
	}))
	if err != nil || result != want {
		t.Fatalf("榜单内容被改写或出错：result=%+v err=%v", result, err)
	}
	raw, err := json.Marshal(result)
	if err != nil {
		t.Fatal(err)
	}
	var decoded model.ShortRanking
	if err := json.Unmarshal(raw, &decoded); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(decoded, *want) {
		t.Fatalf("JSON 未保留目录、跨页名次、推广标记或游标：%s", raw)
	}
}

func TestRankLetsSDKChooseDefaultBoard(t *testing.T) {
	server := &Server{Service: &rankServiceStub{rank: func(_ context.Context, query sdk.ShortRankQuery) (*model.ShortRanking, error) {
		if query != (sdk.ShortRankQuery{}) {
			t.Fatalf("默认查询被注入固定榜单：%+v", query)
		}
		return &model.ShortRanking{Content: "all", Board: "server-selected-board", Items: []model.RankItem{}}, nil
	}}}
	result, err := server.Dispatch(context.Background(), bridgeRequest(t, "rank", nil))
	if err != nil || result.(*model.ShortRanking).Board != "server-selected-board" {
		t.Fatalf("服务端默认榜单未保留：%+v %v", result, err)
	}
}

func TestRankRejectsOversizedSelectorsBeforeServiceCall(t *testing.T) {
	server := &Server{Service: &rankServiceStub{rank: func(context.Context, sdk.ShortRankQuery) (*model.ShortRanking, error) {
		t.Fatal("无效参数仍然请求榜单")
		return nil, nil
	}}}
	for _, args := range []map[string]string{
		{"content": strings.Repeat("x", 101)},
		{"board": strings.Repeat("x", 101)},
		{"cursor": strings.Repeat("x", 16385)},
	} {
		if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "rank", args)); !errors.Is(err, errInvalidInput) {
			t.Fatalf("超长参数未拒绝：%v", err)
		}
	}
}

func TestRankPreservesServiceErrorsAndCancellation(t *testing.T) {
	for _, serviceError := range []error{sdk.ErrContentUnavailable, sdk.ErrTransport, context.Canceled, context.DeadlineExceeded} {
		t.Run(serviceError.Error(), func(t *testing.T) {
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			if serviceError == context.Canceled {
				cancel()
			}
			server := &Server{Service: &rankServiceStub{rank: func(received context.Context, _ sdk.ShortRankQuery) (*model.ShortRanking, error) {
				if received != ctx {
					t.Fatal("榜单请求未沿用取消上下文")
				}
				return nil, serviceError
			}}}
			if _, err := server.Dispatch(ctx, bridgeRequest(t, "rank", nil)); !errors.Is(err, serviceError) {
				t.Fatalf("榜单错误未原样传递：%v", err)
			}
		})
	}
}

func TestRankReportsUnavailableForServiceWithoutRankSupport(t *testing.T) {
	server := &Server{Service: &bridgeServiceStub{}}
	if _, err := server.Dispatch(context.Background(), bridgeRequest(t, "rank", nil)); !errors.Is(err, sdk.ErrContentUnavailable) {
		t.Fatalf("缺少榜单接口时返回意外结果：%v", err)
	}
}
