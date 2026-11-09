package sdk

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"sort"
	"strconv"
	"strings"

	"github.com/beilunyang/openhongguo/internal/protocol"
	"github.com/beilunyang/openhongguo/internal/protocol/sign"
	"github.com/beilunyang/openhongguo/sdk/model"
)

const danmakuMaxDurationMS int64 = 24 * 60 * 60 * 1000

// DanmakuOptions 指定单次弹幕时间窗口。DurationMS 使用当前视频的实际毫秒时长，
// 最大一天；Cursor 仅接受上次返回的数字毫秒位置。每次最多请求 90 条，不自动翻页。
type DanmakuOptions struct {
	Cursor     string
	DurationMS int64
}

// Danmaku 读取一集的一页官方时间轴弹幕，沿用客户端的匿名身份和 POST 签名。
// PositionMS 始终保留服务端的毫秒单位；调用方按播放进度继续请求下一页。
func (c *Client) Danmaku(ctx context.Context, seriesID, episodeID string, opts DanmakuOptions) (*model.DiscussionPage, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if !validID(seriesID) || !validID(episodeID) || opts.DurationMS <= 0 || opts.DurationMS > danmakuMaxDurationMS {
		return nil, fmt.Errorf("%w: invalid danmaku IDs or duration", ErrInvalidOptions)
	}
	start := int64(0)
	if opts.Cursor != "" {
		var ok bool
		start, ok = danmakuInteger(json.RawMessage(strconv.Quote(opts.Cursor)))
		if !ok || start >= opts.DurationMS {
			return nil, fmt.Errorf("%w: invalid danmaku cursor", ErrInvalidOptions)
		}
	}
	body, err := json.Marshal(map[string]any{
		"comment_source": 601, "server_channel": 1000, "group_id": episodeID, "group_type": 30,
		"comment_type": 20, "sort": 1, "count": 90, "cursor": "", "aid": 8662, "compliance_status": 0,
		"business_param": map[string]any{
			"book_id": seriesID, "start_offset_time": start, "playlet_item_duration": opts.DurationMS,
			"need_danmaku_guide_type": []int{1, 3, 4, 2},
		},
	})
	if err != nil {
		return nil, errors.New("openhongguo: danmaku request encoding failed")
	}
	return withFreshDevice(ctx, c, false, func(ctx context.Context) (*model.DiscussionPage, error) {
		profile, err := c.ensureSearchGuest(ctx)
		if err != nil {
			return nil, danmakuRequestError(ctx, err)
		}
		path := "/novel/commentapi/comment/list/" + episodeID + "/v1/"
		ticket := c.clock().UnixMilli()
		query, signed := sign.SignPOST(path, protocol.DeviceQueryPairs(c.identity(profile)), ticket, profile.DeviceID, protocol.VersionName, body, nil)
		headers := anonymousHeaders(c, profile, ticket, signed)
		headers["Comment-Source"], headers["Server-Channel"] = "601", "1000"
		raw, _, err := c.doRaw(ctx, http.MethodPost, c.apiOrigin+path+"?"+query, headers, body)
		if err != nil {
			return nil, danmakuRequestError(ctx, err)
		}
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		return parseDanmakuPage(raw, episodeID, start, opts.DurationMS)
	})
}

// danmakuRequestError 保留可匹配的错误种类，去掉服务端可能回显的请求细节。
func danmakuRequestError(ctx context.Context, err error) error {
	if ctx.Err() != nil {
		return ctx.Err()
	}
	var code *CodeError
	if errors.As(err, &code) {
		copy := *code
		copy.Message, copy.Path = "danmaku request failed", ""
		return &copy
	}
	if errors.Is(err, ErrTransport) {
		return ErrTransport
	}
	var api *APIError
	if errors.As(err, &api) {
		return &APIError{HTTPStatus: api.HTTPStatus, Code: api.Code}
	}
	for _, known := range []error{ErrAuthenticationRequired, ErrRateLimited, ErrAnonymousLimited, ErrDeviceRejected, ErrInvalidOptions, ErrSignerUnavailable, ErrProfileNotFound} {
		if errors.Is(err, known) {
			return known
		}
	}
	return errors.New("openhongguo: danmaku request failed")
}

func parseDanmakuPage(raw []byte, episodeID string, start, duration int64) (*model.DiscussionPage, error) {
	var root struct {
		Code json.RawMessage `json:"code"`
		Data json.RawMessage `json:"data"`
	}
	if json.Unmarshal(raw, &root) != nil {
		return nil, errors.New("openhongguo: malformed danmaku response")
	}
	code, ok := danmakuInteger(root.Code)
	if !ok || code != 0 {
		return nil, errors.New("openhongguo: invalid danmaku response status")
	}
	var data struct {
		List  json.RawMessage `json:"data_list"`
		Extra struct {
			Next json.RawMessage `json:"next_query_danmaku_list_time"`
		} `json:"extra"`
		Info struct {
			HasMore json.RawMessage `json:"has_more"`
			Cursor  json.RawMessage `json:"cursor"`
			Total   json.RawMessage `json:"total"`
		} `json:"common_list_info"`
	}
	if json.Unmarshal(root.Data, &data) != nil || len(bytes.TrimSpace(data.List)) == 0 || bytes.TrimSpace(data.List)[0] != '[' {
		return nil, errors.New("openhongguo: missing or malformed danmaku list")
	}
	var rows []json.RawMessage
	if json.Unmarshal(data.List, &rows) != nil {
		return nil, errors.New("openhongguo: malformed danmaku list")
	}
	page := &model.DiscussionPage{Items: make([]model.DiscussionItem, 0, min(len(rows), 90)), Source: "official"}
	if total, valid := danmakuInteger(data.Info.Total); valid {
		page.Total = &total
	}
	seenIDs := make(map[string]bool)
	type positionText struct {
		Position int64
		Text     string
	}
	seenText := make(map[positionText]bool)
	for _, row := range rows {
		var item struct {
			Comment struct {
				ID     json.RawMessage `json:"comment_id"`
				Common struct {
					GroupID json.RawMessage `json:"group_id"`
					Status  json.RawMessage `json:"status"`
					Content struct {
						Text string `json:"text"`
					} `json:"content"`
				} `json:"common"`
				Expand struct {
					Offset json.RawMessage `json:"offset_time"`
				} `json:"expand"`
				Offset json.RawMessage `json:"offset_time"`
			} `json:"comment"`
		}
		if json.Unmarshal(row, &item) != nil {
			continue
		}
		comment := item.Comment
		if danmakuID(comment.Common.GroupID) != episodeID {
			continue
		}
		status, valid := danmakuInteger(comment.Common.Status)
		if !valid || status != 1 {
			continue
		}
		offset := comment.Expand.Offset
		if len(offset) == 0 || bytes.Equal(bytes.TrimSpace(offset), []byte("null")) {
			offset = comment.Offset
		}
		position, valid := danmakuInteger(offset)
		text := strings.TrimSpace(comment.Common.Content.Text)
		if !valid || position < start || position > duration || text == "" {
			continue
		}
		id := danmakuID(comment.ID)
		key := positionText{Position: position, Text: text}
		if (id != "" && seenIDs[id]) || seenText[key] {
			continue
		}
		if id != "" {
			seenIDs[id] = true
		}
		seenText[key] = true
		page.Items = append(page.Items, model.DiscussionItem{ID: id, Text: text, PositionMS: &position})
	}
	sort.SliceStable(page.Items, func(i, j int) bool { return *page.Items[i].PositionMS < *page.Items[j].PositionMS })
	if len(rows) > 0 && len(page.Items) == 0 {
		page.SourceLimited = true
		page.Note = "本页未返回可用的时间轴弹幕。"
	}

	// 两种游标可能同时出现：选最早的有效推进位置，避免密集窗口跳过未返回的弹幕。
	candidates := []json.RawMessage{data.Extra.Next}
	cursor := bytes.TrimSpace(data.Info.Cursor)
	if len(cursor) > 0 && cursor[0] == '"' {
		var encoded string
		if json.Unmarshal(cursor, &encoded) == nil && len(encoded) <= 4096 {
			cursor = []byte(encoded)
		}
	}
	var fields map[string]json.RawMessage
	if len(cursor) <= 4096 && json.Unmarshal(cursor, &fields) == nil {
		for _, name := range []string{"end_offset_time", "next_offset_time", "offset"} {
			candidates = append(candidates, fields[name])
		}
	}
	hasMore, hasMoreValid := danmakuBool(data.Info.HasMore)
	if hasMoreValid && !hasMore {
		return page, nil
	}
	next := int64(0)
	for _, candidate := range candidates {
		value, valid := danmakuInteger(candidate)
		if valid && value > start && value <= duration && (next == 0 || value < next) {
			next = value
		}
	}
	if next > start && next < duration {
		page.HasMore, page.Cursor = true, strconv.FormatInt(next, 10)
	} else if next != duration {
		page.SourceLimited = true
		page.Note = "弹幕分页位置未推进，本页已保留，请稍后重试。"
		if len(bytes.TrimSpace(data.Info.HasMore)) == 0 && len(cursor) == 0 && len(data.Extra.Next) == 0 {
			page.Note = "来源未返回后续时间段信息，当前内容已保留。"
		}
	}
	return page, nil
}

// danmakuInteger 仅接收非负十进制整数，拒绝小数、科学计数、符号、空白与溢出。
func danmakuInteger(raw json.RawMessage) (int64, bool) {
	text := string(bytes.TrimSpace(raw))
	if len(text) > 0 && text[0] == '"' {
		if json.Unmarshal(raw, &text) != nil {
			return 0, false
		}
	}
	if len(text) == 0 || len(text) > 19 {
		return 0, false
	}
	for _, digit := range text {
		if digit < '0' || digit > '9' {
			return 0, false
		}
	}
	value, err := strconv.ParseInt(text, 10, 64)
	return value, err == nil
}

func danmakuID(raw json.RawMessage) string {
	text := string(bytes.TrimSpace(raw))
	if len(text) > 0 && text[0] == '"' {
		if json.Unmarshal(raw, &text) != nil {
			return ""
		}
	}
	if !validID(text) {
		return ""
	}
	return text
}

func danmakuBool(raw json.RawMessage) (bool, bool) {
	switch string(bytes.TrimSpace(raw)) {
	case "true", "1", `"1"`:
		return true, true
	case "false", "0", `"0"`:
		return false, true
	default:
		return false, false
	}
}
