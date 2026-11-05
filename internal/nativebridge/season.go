package nativebridge

import (
	"context"
	"sort"

	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

type officialSeasonsService interface {
	SeriesSeasons(context.Context, string) ([]model.Season, error)
}

type seasonPage struct {
	SeriesID     string         `json:"series_id"`
	AlbumID      string         `json:"album_id"`
	LinkVerified bool           `json:"link_verified"`
	Seasons      []model.Season `json:"seasons"`
	CurrentIndex int            `json:"current_index"`
	NextSeriesID string         `json:"next_series_id"`
	NextIndex    int            `json:"next_index"`
	HasNext      bool           `json:"has_next"`
	Note         string         `json:"note"`
}

// seasons 只使用官方系列行，并用详情中的同一合集、季序号与可播分集再次核对。
func (s *Server) seasons(ctx context.Context, seriesID string) (*seasonPage, error) {
	service, ok := s.Service.(officialSeasonsService)
	if !ok {
		return nil, sdk.ErrContentUnavailable
	}
	current, err := s.detail(ctx, seriesID)
	if err != nil {
		return nil, err
	}
	result := &seasonPage{SeriesID: seriesID, AlbumID: current.Series.RelatedAlbumID, CurrentIndex: current.Series.SeasonIndex, Seasons: []model.Season{}, Note: "服务端未返回可确认的多季关联"}
	if result.AlbumID == "" || result.CurrentIndex <= 0 {
		return result, nil
	}
	rows, err := service.SeriesSeasons(ctx, seriesID)
	if err != nil {
		return nil, err
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if len(rows) == 0 {
		return result, nil
	}
	ids := make([]string, 0, len(rows))
	seenID, seenIndex := map[string]bool{}, map[int]bool{}
	currentPresent := false
	for _, row := range rows {
		if !validID(row.SeriesID) || row.Index <= 0 || seenID[row.SeriesID] || seenIndex[row.Index] {
			result.Note = "系列剧的季序号存在缺失或重复，请手动选季"
			return result, nil
		}
		seenID[row.SeriesID], seenIndex[row.Index] = true, true
		if row.SeriesID == seriesID {
			if row.Index != result.CurrentIndex {
				result.Note = "当前季在系列剧列表中的序号不一致，请手动选季"
				return result, nil
			}
			currentPresent = true
		}
		ids = append(ids, row.SeriesID)
	}
	if !currentPresent {
		result.Note = "系列剧列表未包含当前季，请手动选季"
		return result, nil
	}
	details, err := s.Service.GetSeriesBatch(ctx, ids)
	if err != nil {
		return nil, err
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	byID := make(map[string]model.SeriesDetail, len(details))
	for _, detail := range details {
		if detail.Series != nil {
			byID[detail.Series.ID] = detail
		}
	}
	hasPreview := false
	for _, row := range rows {
		detail, exists := byID[row.SeriesID]
		if exists && detail.Series.RelatedAlbumID == "" && detail.Series.SeasonIndex == row.Index && row.EpisodeCount == 0 && len(detail.Episodes) == 0 && row.SeriesID != seriesID {
			// 官方列表会提前列出未上线季；其空详情不阻止已确认的前几季连续播放。
			result.Seasons = append(result.Seasons, row)
			hasPreview = true
			continue
		}
		if !exists || detail.Series.RelatedAlbumID != result.AlbumID || detail.Series.SeasonIndex != row.Index {
			result.Seasons = []model.Season{}
			result.Note = "部分季的官方合集关联尚未确认，请手动选季"
			return result, nil
		}
		result.Seasons = append(result.Seasons, model.Season{SeriesID: row.SeriesID, Title: detail.Series.Title, Index: row.Index, CoverURL: detail.Series.CoverURL, EpisodeCount: len(detail.Episodes), Status: detail.Series.Status})
	}
	sort.Slice(result.Seasons, func(i, j int) bool { return result.Seasons[i].Index < result.Seasons[j].Index })
	result.LinkVerified = true
	result.Note = "此系列暂无可自动续播的下一季"
	if seenIndex[result.CurrentIndex+1] {
		result.Note = "下一季尚未返回可确认的播放信息，播完本季后暂停"
	}
	if current.Series.Status != model.SeriesEnd {
		result.Note = "当前季尚未完结，播至已更新末集后暂停等待更新"
		return result, nil
	}
	for _, season := range result.Seasons {
		if season.Index != result.CurrentIndex+1 {
			continue
		}
		next := byID[season.SeriesID]
		for _, episode := range next.Episodes {
			if episode.Playable && validID(episode.ID) && episode.SeriesID == season.SeriesID && episode.Number == 1 {
				result.NextSeriesID, result.NextIndex, result.HasNext = season.SeriesID, season.Index, true
				result.Note = "本季播放结束后将续播下一季"
				if hasPreview {
					result.Note = "已上线季的合集关联已核实，未上线季仅展示预告；本季播放结束后将续播下一季"
				}
				return result, nil
			}
		}
		result.Note = "下一季尚未上线或首集暂不可播，播完本季后暂停"
		return result, nil
	}
	return result, nil
}
