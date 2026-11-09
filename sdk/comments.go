package sdk

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/beilunyang/openhongguo/sdk/model"
)

const webCommentsOrigin = "https://hongguoduanju.com"
const webCommentsNote = "官网仅展示本集部分评论，更多评论请在红果短剧 App 查看。"

var webRouterAssignment = regexp.MustCompile(`\b(?:window\.)?_ROUTER_DATA\s*=\s*`)

// WebEpisodeComments 读取官方播放页公开的本集评论预览。
// 网页未提供分页游标，因此本方法只返回一次预览，不把总评论数当作可加载条数。
func (c *Client) WebEpisodeComments(ctx context.Context, seriesID, episodeID string) (*model.DiscussionPage, error) {
	if !validID(seriesID) || !validID(episodeID) {
		return nil, fmt.Errorf("%w: invalid comment identifiers", ErrInvalidOptions)
	}
	ctx, cancel := context.WithTimeout(ctx, 12*time.Second)
	defer cancel()
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	// 公开网页使用独立固定主机，不附加 App 的账号、游客 Cookie 或设备参数。
	response, err := c.transport.Do(ctx, &Request{
		Method: "GET",
		URL:    webCommentsOrigin + "/player/" + seriesID + "/" + episodeID,
		Headers: map[string]string{
			"Accept":     "text/html",
			"Referer":    webCommentsOrigin + "/",
			"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
		},
	})
	if err != nil {
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		return nil, fmt.Errorf("%w: public comments request failed", ErrTransport)
	}
	body, err := readAnonymous(response)
	if err != nil {
		return nil, err
	}
	if len(body) >= 8<<20 {
		return nil, fmt.Errorf("%w: public comments response is too large", ErrTransport)
	}
	return parseWebEpisodeComments(body, seriesID, episodeID)
}

type webCommentReview struct {
	Text      string          `json:"content"`
	Author    string          `json:"user_name"`
	AvatarURL string          `json:"user_avatar"`
	CreatedAt json.RawMessage `json:"create_time"`
	LikeCount json.RawMessage `json:"digg_count"`
}

func parseWebEpisodeComments(body []byte, seriesID, episodeID string) (*model.DiscussionPage, error) {
	assignment := webRouterAssignment.FindIndex(body)
	if assignment == nil {
		return nil, fmt.Errorf("%w: public comments data is missing", ErrContentUnavailable)
	}
	var router struct {
		LoaderData map[string]json.RawMessage `json:"loaderData"`
	}
	// 只解码赋值右侧的 JSON，不执行脚本，也不依赖 JSON 后的 script 标签格式。
	decoder := json.NewDecoder(bytes.NewReader(body[assignment[1]:]))
	if decoder.Decode(&router) != nil {
		return nil, fmt.Errorf("%w: public comments data is malformed", ErrContentUnavailable)
	}
	for _, raw := range router.LoaderData {
		var page struct {
			IsSuccess *bool  `json:"isSuccess"`
			EpisodeID string `json:"vid"`
			Series    struct {
				ID string `json:"series_id"`
			} `json:"seriesDetail"`
			Social struct {
				Reviews struct {
					Total json.RawMessage `json:"total_count"`
					Items json.RawMessage `json:"reviews"`
				} `json:"reviews"`
			} `json:"episodeSocialInfo"`
		}
		if json.Unmarshal(raw, &page) != nil || page.Series.ID != seriesID || page.EpisodeID != episodeID {
			continue
		}
		if page.IsSuccess != nil && !*page.IsSuccess {
			return nil, fmt.Errorf("%w: public comments page was not successful", ErrContentUnavailable)
		}
		rows := bytes.TrimSpace(page.Social.Reviews.Items)
		if len(rows) == 0 || bytes.Equal(rows, []byte("null")) {
			return nil, fmt.Errorf("%w: public comments preview is missing", ErrContentUnavailable)
		}
		var reviews []webCommentReview
		if json.Unmarshal(rows, &reviews) != nil {
			return nil, fmt.Errorf("%w: public comments preview is malformed", ErrContentUnavailable)
		}
		result := &model.DiscussionPage{
			Items:         []model.DiscussionItem{},
			Total:         webCommentCount(page.Social.Reviews.Total),
			SourceLimited: true,
			Source:        "official_web",
			Note:          webCommentsNote,
		}
		if len(reviews) == 0 && result.Total == nil {
			return nil, fmt.Errorf("%w: public comments empty result is unconfirmed", ErrContentUnavailable)
		}
		seen := make(map[string]bool)
		for _, review := range reviews {
			text := strings.TrimSpace(review.Text)
			if text == "" {
				continue
			}
			createdAt := int64(0)
			if value := webCommentCount(review.CreatedAt); value != nil {
				createdAt = *value
				if createdAt >= 1_000_000_000_000 {
					createdAt /= 1000
				}
			}
			// 公开预览没有评论 ID；只用稳定字段生成本地身份，点赞数变化不产生新条目。
			identity, _ := json.Marshal([]string{seriesID, episodeID, review.Author, strconv.FormatInt(createdAt, 10), text})
			hash := sha256.Sum256(identity)
			id := hex.EncodeToString(hash[:])
			if seen[id] {
				continue
			}
			seen[id] = true
			avatar := ""
			if parsed, err := url.Parse(review.AvatarURL); err == nil && parsed.Hostname() != "" && parsed.User == nil && (parsed.Scheme == "https" || parsed.Scheme == "http") && len(review.AvatarURL) <= 4096 {
				avatar = review.AvatarURL
			}
			result.Items = append(result.Items, model.DiscussionItem{
				ID: id, Author: review.Author, Text: text, AvatarURL: avatar,
				LikeCount: webCommentCount(review.LikeCount), CreatedAt: createdAt,
			})
			if len(result.Items) >= 100 {
				break
			}
		}
		if len(reviews) > 0 && len(result.Items) == 0 {
			return nil, fmt.Errorf("%w: public comments preview has no readable entries", ErrContentUnavailable)
		}
		return result, nil
	}
	return nil, fmt.Errorf("%w: public comments episode does not match", ErrContentUnavailable)
}

func webCommentCount(raw json.RawMessage) *int64 {
	value := strings.TrimSpace(string(raw))
	if strings.HasPrefix(value, `"`) {
		if json.Unmarshal(raw, &value) != nil {
			return nil
		}
	}
	count, err := strconv.ParseInt(value, 10, 64)
	if err != nil || count < 0 {
		return nil
	}
	return &count
}
