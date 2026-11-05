package nativebridge

import (
	"context"
	"encoding/json"
	"errors"
	"strconv"
	"strings"

	"github.com/beilunyang/openhongguo/internal/protocol"
	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

type recommendationPage struct {
	sdk.Page[model.SeriesSummary]
	Source         string `json:"source"`
	SourceLabel    string `json:"source_label"`
	FallbackReason string `json:"fallback_reason,omitempty"`
}

type recommendationCursor struct {
	Method  string `json:"method"`
	Source  string `json:"source"`
	Channel string `json:"channel"`
	Cursor  string `json:"cursor"`
}

const recommendationCursorPrefix = "catalog.v1:"

// catalog 保留推荐接口的真实结果；只有明确的空内容错误才改取同类型的热度目录。
func (s *Server) catalog(ctx context.Context, method, channel, cursor, seriesID string) (*recommendationPage, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	resolved, err := protocol.ResolveDiscoverChannel(channel)
	if err != nil {
		return nil, errInvalidInput
	}
	channel = resolved.Name
	continuation := recommendationCursor{Method: method, Source: "discover", Channel: channel}
	if cursor != "" {
		if strings.HasPrefix(cursor, recommendationCursorPrefix) {
			if json.Unmarshal([]byte(strings.TrimPrefix(cursor, recommendationCursorPrefix)), &continuation) != nil || continuation.Method != method || continuation.Cursor == "" || continuation.Channel != channel || (continuation.Source != "discover" && continuation.Source != "filter") {
				return nil, errInvalidInput
			}
		} else if method == "discover" {
			// 老版本的发现页游标仍交回 SDK 验证；目录游标从不走此兼容路径。
			legacy, err := protocol.DecodeFeedCursor(cursor)
			if err != nil || legacy.TabType != strconv.Itoa(resolved.ResponseTab) {
				return nil, errInvalidInput
			}
			continuation.Cursor = cursor
		} else {
			return nil, errInvalidInput
		}
	}
	if continuation.Source == "filter" {
		return s.recommendFromFilter(ctx, method, channel, continuation.Cursor, seriesID)
	}

	page, err := s.discover(ctx, channel, continuation.Cursor, 1)
	if errors.Is(err, sdk.ErrEndOfList) && recommendationContentMissing(err) {
		return finishRecommendation(&sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{}}, method, "discover", channel, ""), nil
	}
	if err != nil && !recommendationContentMissing(err) {
		return nil, err
	}
	if err == nil {
		if len(page.Items) > 0 && cursor == "" && validID(seriesID) && (channel == "推荐" || channel == "找剧") {
			related, relatedErr := s.Service.SeriesRecommend(ctx, seriesID)
			if relatedErr != nil && !recommendationContentMissing(relatedErr) {
				return nil, relatedErr
			}
			if related != nil {
				page.Items = append(related.Items, page.Items...)
			}
		}
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		page.Items = unique(page.Items, seriesID)
		s.applyCached(page.Items)
		return finishRecommendation(page, method, "discover", channel, ""), nil
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	switch channel {
	case "推荐", "找剧", "真人剧", "首页真人剧", "漫剧", "首页漫剧":
	default:
		// 电影等频道缺少对应目录接口，保留原始错误，不混入短剧。
		return nil, err
	}

	// 目录回退有自己的分页协议；之后只续接目录，不把游标送回发现接口。
	fallback, fallbackErr := s.recommendFromFilter(ctx, method, channel, "", seriesID)
	if fallbackErr == nil {
		return fallback, nil
	}
	if !recommendationContentMissing(fallbackErr) {
		return nil, fallbackErr
	}
	if validID(seriesID) && (channel == "推荐" || channel == "找剧") {
		related, relatedErr := s.Service.SeriesRecommend(ctx, seriesID)
		if relatedErr != nil && !recommendationContentMissing(relatedErr) {
			return nil, relatedErr
		}
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		if related != nil {
			items := unique(related.Items, seriesID)
			if len(items) > 0 {
				s.applyCached(items)
				return &recommendationPage{Page: sdk.Page[model.SeriesSummary]{Items: items}, Source: "related", SourceLabel: "更多好剧", FallbackReason: "discover_empty"}, nil
			}
		}
	}
	return nil, errors.Join(errRecommendationEmpty, sdk.ErrAnonymousLimited)
}

func (s *Server) recommendFromFilter(ctx context.Context, method, channel, cursor, seriesID string) (*recommendationPage, error) {
	selections := map[string][]string{"sort": {"hot_score"}}
	switch channel {
	case "推荐", "找剧":
	case "真人剧", "首页真人剧":
		selections["genre"] = []string{"short_play"}
	case "漫剧", "首页漫剧":
		selections["genre"] = []string{"comic_series"}
	default:
		return nil, sdk.ErrContentUnavailable
	}
	service, ok := s.Service.(shortFilterService)
	if !ok {
		return nil, sdk.ErrContentUnavailable
	}
	page, err := service.ShortFilter(ctx, sdk.ShortFilterQuery{Selections: selections, Cursor: cursor})
	if errors.Is(err, sdk.ErrEndOfList) && recommendationContentMissing(err) {
		return finishRecommendation(&sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{}}, method, "filter", channel, "discover_empty"), nil
	}
	if err != nil {
		return nil, err
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if page == nil || len(page.Items) == 0 {
		if cursor == "" {
			return nil, sdk.ErrContentUnavailable
		}
		return finishRecommendation(&sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{}}, method, "filter", channel, "discover_empty"), nil
	}
	items := unique(page.Items, seriesID)
	s.applyCached(items)
	result := &sdk.Page[model.SeriesSummary]{Items: items, Cursor: page.Cursor, HasMore: page.HasMore && page.Cursor != cursor}
	return finishRecommendation(result, method, "filter", channel, "discover_empty"), nil
}

func finishRecommendation(page *sdk.Page[model.SeriesSummary], method, source, channel, reason string) *recommendationPage {
	result := &recommendationPage{Page: *page, Source: source, SourceLabel: "红果推荐", FallbackReason: reason}
	if source == "filter" {
		result.SourceLabel = "热门目录（推荐暂未返回）"
	}
	result.HasMore = page.HasMore && page.Cursor != ""
	result.Cursor = ""
	if result.HasMore {
		raw, _ := json.Marshal(recommendationCursor{Method: method, Source: source, Channel: channel, Cursor: page.Cursor})
		result.Cursor = recommendationCursorPrefix + string(raw)
	}
	return result
}

func recommendationContentMissing(err error) bool {
	// 账号、验证、设备、限流及取消错误必须原样返回，不作为换来源的理由。
	for _, blocked := range []error{context.Canceled, context.DeadlineExceeded, sdk.ErrAuthenticationRequired, sdk.ErrChallengeRequired, sdk.ErrDeviceRejected, sdk.ErrRateLimited, sdk.ErrSignatureRejected, sdk.ErrTransport} {
		if errors.Is(err, blocked) {
			return false
		}
	}
	return errors.Is(err, sdk.ErrAnonymousLimited) || errors.Is(err, sdk.ErrContentUnavailable) || errors.Is(err, sdk.ErrEndOfList)
}
