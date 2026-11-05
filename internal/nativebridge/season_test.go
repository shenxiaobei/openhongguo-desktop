package nativebridge

import (
	"context"
	"errors"
	"reflect"
	"testing"

	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

type seasonsServiceStub struct {
	bridgeServiceStub
	seasons func(context.Context, string) ([]model.Season, error)
}

func (s *seasonsServiceStub) SeriesSeasons(ctx context.Context, id string) ([]model.Season, error) {
	return s.seasons(ctx, id)
}

func seasonDetail(id, album string, index int) model.SeriesDetail {
	return model.SeriesDetail{Series: &model.Series{SeriesSummary: bridgeItem(id), SeasonIndex: index, RelatedAlbumID: album, Status: model.SeriesEnd}, Episodes: []model.Episode{{ID: id + "01", SeriesID: id, Number: 1, Playable: true}}}
}

func TestSeasonsRequiresOfficialSameAlbumAndUniqueAdjacentPlayableSeason(t *testing.T) {
	rows := []model.Season{{SeriesID: "1003", Index: 3}, {SeriesID: "1001", Index: 1}, {SeriesID: "1002", Index: 2}}
	details := []model.SeriesDetail{seasonDetail("1001", "9001", 1), seasonDetail("1002", "9001", 2), seasonDetail("1003", "9001", 3)}
	service := &seasonsServiceStub{seasons: func(_ context.Context, id string) ([]model.Season, error) {
		if id != "1001" {
			t.Fatalf("官方系列剧查询ID不正确：%s", id)
		}
		return rows, nil
	}}
	service.detail = func(_ context.Context, ids []string) ([]model.SeriesDetail, error) {
		if len(ids) == 1 {
			return details[:1], nil
		}
		return details, nil
	}
	result, err := (&Server{Service: service}).Dispatch(context.Background(), bridgeRequest(t, "seasons", map[string]string{"id": "1001"}))
	if err != nil {
		t.Fatal(err)
	}
	page := result.(*seasonPage)
	if !page.LinkVerified || !page.HasNext || page.NextSeriesID != "1002" || page.NextIndex != 2 || page.CurrentIndex != 1 || page.AlbumID != "9001" {
		t.Fatalf("可靠的相邻季未被正确确认：%+v", page)
	}
	if got := []int{page.Seasons[0].Index, page.Seasons[1].Index, page.Seasons[2].Index}; !reflect.DeepEqual(got, []int{1, 2, 3}) {
		t.Fatalf("季顺序被改变：%v", got)
	}
}

func TestSeasonsAnnouncedLaterSeasonDoesNotBlockVerifiedNextSeason(t *testing.T) {
	service := &seasonsServiceStub{seasons: func(context.Context, string) ([]model.Season, error) {
		return []model.Season{{SeriesID: "1001", Index: 1, EpisodeCount: 1}, {SeriesID: "1002", Index: 2, EpisodeCount: 1}, {SeriesID: "1003", Index: 3}}, nil
	}}
	service.detail = func(_ context.Context, ids []string) ([]model.SeriesDetail, error) {
		current, next := seasonDetail("1001", "9001", 1), seasonDetail("1002", "9001", 2)
		if len(ids) == 1 {
			return []model.SeriesDetail{current}, nil
		}
		return []model.SeriesDetail{current, next, {Series: &model.Series{SeriesSummary: bridgeItem("1003"), SeasonIndex: 3}}}, nil
	}
	result, err := (&Server{Service: service}).Dispatch(context.Background(), bridgeRequest(t, "seasons", map[string]string{"id": "1001"}))
	if err != nil {
		t.Fatal(err)
	}
	page := result.(*seasonPage)
	if !page.LinkVerified || !page.HasNext || page.NextSeriesID != "1002" || len(page.Seasons) != 3 || page.Seasons[2].EpisodeCount != 0 {
		t.Fatalf("末尾预告季阻止了已确认的下一季：%+v", page)
	}
}

func TestSeasonsStopsAtAnnouncedAdjacentSeason(t *testing.T) {
	service := &seasonsServiceStub{seasons: func(context.Context, string) ([]model.Season, error) {
		return []model.Season{{SeriesID: "1004", Index: 4, EpisodeCount: 1}, {SeriesID: "1005", Index: 5}, {SeriesID: "1006", Index: 6, EpisodeCount: 1}}, nil
	}}
	service.detail = func(_ context.Context, ids []string) ([]model.SeriesDetail, error) {
		current := seasonDetail("1004", "9001", 4)
		if len(ids) == 1 {
			return []model.SeriesDetail{current}, nil
		}
		return []model.SeriesDetail{current, {Series: &model.Series{SeriesSummary: bridgeItem("1005"), SeasonIndex: 5}}, seasonDetail("1006", "9001", 6)}, nil
	}
	result, err := (&Server{Service: service}).Dispatch(context.Background(), bridgeRequest(t, "seasons", map[string]string{"id": "1004"}))
	if err != nil {
		t.Fatal(err)
	}
	page := result.(*seasonPage)
	if !page.LinkVerified || page.HasNext || page.NextSeriesID != "" || len(page.Seasons) != 3 || page.Seasons[1].Index != 5 || page.Seasons[1].EpisodeCount != 0 {
		t.Fatalf("预告第五季被越过：%+v", page)
	}
}

func TestSeasonsDoesNotJumpAcrossAmbiguousMissingOrUnavailableSeasons(t *testing.T) {
	for _, test := range []struct {
		name     string
		mutate   func(*[]model.Season, *[]model.SeriesDetail)
		verified bool
	}{
		{name: "缺少当前季", mutate: func(rows *[]model.Season, _ *[]model.SeriesDetail) { *rows = (*rows)[1:] }},
		{name: "重复序号", mutate: func(rows *[]model.Season, _ *[]model.SeriesDetail) { (*rows)[2].Index = 2 }},
		{name: "未知序号", mutate: func(rows *[]model.Season, _ *[]model.SeriesDetail) { (*rows)[1].Index = 0 }},
		{name: "重复剧集ID", mutate: func(rows *[]model.Season, _ *[]model.SeriesDetail) { (*rows)[2].SeriesID = "1002" }},
		{name: "相似标题不同合集", mutate: func(_ *[]model.Season, details *[]model.SeriesDetail) { (*details)[1].Series.RelatedAlbumID = "9002" }},
		{name: "候选合集缺失", mutate: func(_ *[]model.Season, details *[]model.SeriesDetail) { (*details)[1].Series.RelatedAlbumID = "" }},
		{name: "候选详情序号冲突", mutate: func(_ *[]model.Season, details *[]model.SeriesDetail) { (*details)[1].Series.SeasonIndex = 4 }},
		{name: "候选详情返回其他ID", mutate: func(_ *[]model.Season, details *[]model.SeriesDetail) { (*details)[1].Series.ID = "1009" }},
		{name: "缺下一季不能跳第三季", verified: true, mutate: func(rows *[]model.Season, details *[]model.SeriesDetail) {
			*rows = append((*rows)[:1], (*rows)[2])
			*details = append((*details)[:1], (*details)[2])
		}},
		{name: "当前季仍连载", verified: true, mutate: func(_ *[]model.Season, details *[]model.SeriesDetail) {
			(*details)[0].Series.Status = model.SeriesUpdating
		}},
		{name: "下一季尚未上线", verified: true, mutate: func(_ *[]model.Season, details *[]model.SeriesDetail) { (*details)[1].Episodes = nil }},
		{name: "下一季第一集受限", verified: true, mutate: func(_ *[]model.Season, details *[]model.SeriesDetail) {
			(*details)[1].Episodes[0].Playable = false
			(*details)[1].Episodes = append((*details)[1].Episodes, model.Episode{ID: "100202", SeriesID: "1002", Number: 2, Playable: true})
		}},
		{name: "第一集属于别的剧", verified: true, mutate: func(_ *[]model.Season, details *[]model.SeriesDetail) { (*details)[1].Episodes[0].SeriesID = "1009" }},
	} {
		t.Run(test.name, func(t *testing.T) {
			rows := []model.Season{{SeriesID: "1001", Index: 1}, {SeriesID: "1002", Index: 2}, {SeriesID: "1003", Index: 3}}
			details := []model.SeriesDetail{seasonDetail("1001", "9001", 1), seasonDetail("1002", "9001", 2), seasonDetail("1003", "9001", 3)}
			test.mutate(&rows, &details)
			service := &seasonsServiceStub{seasons: func(context.Context, string) ([]model.Season, error) { return rows, nil }}
			service.detail = func(_ context.Context, ids []string) ([]model.SeriesDetail, error) {
				if len(ids) == 1 {
					return details[:1], nil
				}
				return details, nil
			}
			result, err := (&Server{Service: service}).Dispatch(context.Background(), bridgeRequest(t, "seasons", map[string]string{"id": "1001"}))
			if err != nil {
				t.Fatal(err)
			}
			page := result.(*seasonPage)
			if page.HasNext || page.NextSeriesID != "" || page.NextIndex != 0 || page.LinkVerified != test.verified || page.Note == "" {
				t.Fatalf("存在歧义仍然自动续季：%+v", page)
			}
		})
	}
}

func TestSeasonsPreservesErrorsCancellationAndMissingCapability(t *testing.T) {
	for _, stage := range []string{"current", "official", "verification"} {
		for _, upstream := range []error{context.Canceled, context.DeadlineExceeded, sdk.ErrRateLimited, sdk.ErrAuthenticationRequired, sdk.ErrChallengeRequired, sdk.ErrDeviceRejected, sdk.ErrTransport, sdk.ErrContentUnavailable} {
			calls := 0
			service := &seasonsServiceStub{seasons: func(context.Context, string) ([]model.Season, error) {
				if stage == "official" {
					return nil, upstream
				}
				return []model.Season{{SeriesID: "1001", Index: 1}, {SeriesID: "1002", Index: 2}}, nil
			}}
			service.detail = func(context.Context, []string) ([]model.SeriesDetail, error) {
				calls++
				if stage == "current" || stage == "verification" && calls == 2 {
					return nil, upstream
				}
				return []model.SeriesDetail{seasonDetail("1001", "9001", 1), seasonDetail("1002", "9001", 2)}, nil
			}
			if _, err := (&Server{Service: service}).Dispatch(context.Background(), bridgeRequest(t, "seasons", map[string]string{"id": "1001"})); !errors.Is(err, upstream) {
				t.Fatalf("阶段%s错误传播丢失：%v", stage, err)
			}
		}
	}
	if _, err := (&Server{Service: &bridgeServiceStub{}}).Dispatch(context.Background(), bridgeRequest(t, "seasons", map[string]string{"id": "1001"})); !errors.Is(err, sdk.ErrContentUnavailable) {
		t.Fatalf("缺少严格系列能力未报告：%v", err)
	}
}
