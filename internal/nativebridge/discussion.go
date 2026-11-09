package nativebridge

import (
	"context"
	"strconv"
	"strings"

	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

type episodeCommentsService interface {
	WebEpisodeComments(context.Context, string, string) (*model.DiscussionPage, error)
}

type danmakuService interface {
	Danmaku(context.Context, string, string, sdk.DanmakuOptions) (*model.DiscussionPage, error)
}

func (s *Server) discussion(ctx context.Context, method, seriesID, episodeID, cursor string, durationMS int64) (*model.DiscussionPage, error) {
	if !validID(seriesID) || !validID(episodeID) {
		return nil, errInvalidInput
	}
	if method == "comments" && (cursor != "" || durationMS != 0) {
		return nil, errInvalidInput
	}
	if method == "danmaku" {
		if durationMS < 1 || durationMS > 86400000 {
			return nil, errInvalidInput
		}
		if cursor != "" {
			position, err := strconv.ParseInt(cursor, 10, 64)
			if err != nil || len(cursor) > 19 || strings.Trim(cursor, "0123456789") != "" || position < 0 || position >= durationMS {
				return nil, errInvalidInput
			}
		}
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	var page *model.DiscussionPage
	var err error
	switch method {
	case "comments":
		service, ok := s.Service.(episodeCommentsService)
		if !ok {
			return nil, sdk.ErrContentUnavailable
		}
		page, err = service.WebEpisodeComments(ctx, seriesID, episodeID)
	case "danmaku":
		service, ok := s.Service.(danmakuService)
		if !ok {
			return nil, sdk.ErrContentUnavailable
		}
		page, err = service.Danmaku(ctx, seriesID, episodeID, sdk.DanmakuOptions{Cursor: cursor, DurationMS: durationMS})
	default:
		return nil, errInvalidInput
	}
	if err != nil {
		return nil, err
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if page == nil {
		return nil, sdk.ErrContentUnavailable
	}
	// 空结果保持未知总数，只规范数组形态，避免把缺失内容伪造成零条。
	if page.Items == nil {
		page.Items = []model.DiscussionItem{}
	}
	return page, nil
}
