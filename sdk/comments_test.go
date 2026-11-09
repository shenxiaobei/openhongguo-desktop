package sdk_test

import (
	"bytes"
	"compress/gzip"
	"context"
	"encoding/json"
	"errors"
	"strings"
	"testing"

	"github.com/beilunyang/openhongguo/adapters/memstore"
	"github.com/beilunyang/openhongguo/sdk"
)

type commentsTransportFunc func(context.Context, *sdk.Request) (*sdk.Response, error)

func (f commentsTransportFunc) Do(ctx context.Context, request *sdk.Request) (*sdk.Response, error) {
	return f(ctx, request)
}

type commentsAccountProvider struct{ t *testing.T }

func (p commentsAccountProvider) Apply(context.Context, *sdk.Request) error {
	p.t.Fatal("公开评论请求不应调用账号提供器")
	return nil
}

func commentsClient(t *testing.T, transport commentsTransportFunc) *sdk.Client {
	t.Helper()
	client, err := sdk.NewClient(sdk.Options{
		Transport: transport, ProfileStore: memstore.New(), AuthProvider: commentsAccountProvider{t},
	})
	if err != nil {
		t.Fatal(err)
	}
	return client
}

func commentsHTML(t *testing.T, seriesID, episodeID string, reviews any) []byte {
	t.Helper()
	router := map[string]any{"loaderData": map[string]any{
		"layout": nil,
		"player/page": map[string]any{
			"isSuccess": true, "vid": episodeID,
			"seriesDetail":      map[string]any{"series_id": seriesID},
			"episodeSocialInfo": map[string]any{"reviews": reviews},
		},
	}}
	data, err := json.Marshal(router)
	if err != nil {
		t.Fatal(err)
	}
	return append(append([]byte("<html><script>window._ROUTER_DATA = "), data...), []byte(";ignoredJavascript();</script></html>")...)
}

func TestWebEpisodeCommentsReadsActualPreviewWithoutCredentials(t *testing.T) {
	const seriesID, episodeID = "7681212396911741976", "7681231144708230168"
	likeCount := "4"
	calls := 0
	client := commentsClient(t, func(ctx context.Context, request *sdk.Request) (*sdk.Response, error) {
		calls++
		if request.Method != "GET" || request.URL != "https://hongguoduanju.com/player/"+seriesID+"/"+episodeID || len(request.Body) != 0 {
			t.Fatalf("公开评论请求不匹配: %#v", request)
		}
		for key := range request.Headers {
			if strings.EqualFold(key, "Cookie") || strings.EqualFold(key, "Authorization") || strings.EqualFold(key, "X-Tt-Token") {
				t.Fatalf("公开评论请求包含凭据头 %s", key)
			}
		}
		if request.Headers["Accept"] != "text/html" || request.Headers["Referer"] != "https://hongguoduanju.com/" {
			t.Fatalf("公开网页请求头不匹配: %#v", request.Headers)
		}
		if _, ok := ctx.Deadline(); !ok {
			t.Fatal("公开评论请求未设置超时")
		}
		review := map[string]any{"content": "  引号\"和括号 } {，保留正文 😀  ", "user_name": "测试作者", "user_avatar": "https://images.example/avatar.jpg", "create_time": "1700000000123", "digg_count": likeCount}
		body := commentsHTML(t, seriesID, episodeID, map[string]any{"total_count": "1300", "reviews": []any{
			review, review,
			map[string]any{"content": "另一条", "create_time": "invalid", "digg_count": -1, "user_avatar": "javascript:alert(1)"},
			map[string]any{"content": "  "},
		}})
		return &sdk.Response{StatusCode: 200, Body: body}, nil
	})
	page, err := client.WebEpisodeComments(context.Background(), seriesID, episodeID)
	if err != nil {
		t.Fatal(err)
	}
	if page.Total == nil || *page.Total != 1300 || len(page.Items) != 2 || page.HasMore || page.Cursor != "" || !page.SourceLimited || page.Source != "official_web" || !strings.Contains(page.Note, "部分评论") {
		t.Fatalf("预览状态不正确: %+v", page)
	}
	first := page.Items[0]
	if first.Text != "引号\"和括号 } {，保留正文 😀" || first.Author != "测试作者" || first.AvatarURL != "https://images.example/avatar.jpg" || first.LikeCount == nil || *first.LikeCount != 4 || first.CreatedAt != 1700000000 || len(first.ID) != 64 || first.PositionMS != nil {
		t.Fatalf("评论字段不匹配: %+v", first)
	}
	if second := page.Items[1]; second.AvatarURL != "" || second.CreatedAt != 0 || second.LikeCount != nil {
		t.Fatalf("缺失字段没有保留未知状态: %+v", second)
	}
	likeCount = "8"
	updated, err := client.WebEpisodeComments(context.Background(), seriesID, episodeID)
	if err != nil || updated.Items[0].ID != first.ID || *updated.Items[0].LikeCount != 8 || calls != 2 {
		t.Fatalf("点赞更新改变了评论身份或请求失败: %+v, %v", updated, err)
	}
}

func TestWebEpisodeCommentsDistinguishesZeroFromUnavailable(t *testing.T) {
	const seriesID, episodeID = "7681212396911741976", "7681231144708230168"
	tests := []struct {
		name    string
		reviews any
		valid   bool
	}{
		{"真实零评论", map[string]any{"total_count": 0, "reviews": []any{}}, true},
		{"缺失容器", nil, false},
		{"缺失数组", map[string]any{"total_count": "1300"}, false},
		{"null数组", map[string]any{"total_count": "0", "reviews": nil}, false},
		{"未知空结果", map[string]any{"reviews": []any{}}, false},
		{"错误数组结构", map[string]any{"total_count": "1", "reviews": map[string]any{}}, false},
		{"全部正文无效", map[string]any{"total_count": "1", "reviews": []any{map[string]any{"content": " "}}}, false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			client := commentsClient(t, func(context.Context, *sdk.Request) (*sdk.Response, error) {
				return &sdk.Response{StatusCode: 200, Body: commentsHTML(t, seriesID, episodeID, tt.reviews)}, nil
			})
			page, err := client.WebEpisodeComments(context.Background(), seriesID, episodeID)
			if !tt.valid {
				if page != nil || !errors.Is(err, sdk.ErrContentUnavailable) {
					t.Fatalf("缺失数据被当作正常空页: %+v, %v", page, err)
				}
				return
			}
			if err != nil || page.Total == nil || *page.Total != 0 || page.Items == nil || len(page.Items) != 0 {
				t.Fatalf("真实零评论解析失败: %+v, %v", page, err)
			}
		})
	}
}

func TestWebEpisodeCommentsRejectsWrongEpisodeAndMalformedHTML(t *testing.T) {
	const seriesID, episodeID = "7681212396911741976", "7681231144708230168"
	preview := map[string]any{"total_count": "0", "reviews": []any{}}
	bodies := [][]byte{
		commentsHTML(t, "7681212396911741977", episodeID, preview),
		commentsHTML(t, seriesID, "7681231144708230169", preview),
		[]byte("<html>暂无页面数据</html>"),
		[]byte("<script>_ROUTER_DATA = {broken JSON</script>"),
		bytes.Replace(commentsHTML(t, seriesID, episodeID, preview), []byte(`"isSuccess":true`), []byte(`"isSuccess":false`), 1),
	}
	for _, body := range bodies {
		client := commentsClient(t, func(context.Context, *sdk.Request) (*sdk.Response, error) {
			return &sdk.Response{StatusCode: 200, Body: body}, nil
		})
		if page, err := client.WebEpisodeComments(context.Background(), seriesID, episodeID); page != nil || !errors.Is(err, sdk.ErrContentUnavailable) {
			t.Fatalf("无效页面被接受: %+v, %v", page, err)
		}
	}
}

func TestWebEpisodeCommentsPreservesHTTPErrorsAndCancellation(t *testing.T) {
	const seriesID, episodeID = "7681212396911741976", "7681231144708230168"
	for _, status := range []int{403, 429, 503} {
		client := commentsClient(t, func(context.Context, *sdk.Request) (*sdk.Response, error) {
			return &sdk.Response{StatusCode: status, Body: []byte("error")}, nil
		})
		page, err := client.WebEpisodeComments(context.Background(), seriesID, episodeID)
		if page != nil || err == nil || status == 403 && !errors.Is(err, sdk.ErrAuthenticationRequired) || status == 429 && !errors.Is(err, sdk.ErrRateLimited) {
			t.Fatalf("HTTP %d 错误处理失败: %+v, %v", status, page, err)
		}
	}
	calls := 0
	client := commentsClient(t, func(context.Context, *sdk.Request) (*sdk.Response, error) {
		calls++
		return nil, errors.New("transport secret that must not reach callers")
	})
	for _, id := range []string{"../bad", "123", "12345?token=value"} {
		if _, err := client.WebEpisodeComments(context.Background(), id, episodeID); !errors.Is(err, sdk.ErrInvalidOptions) {
			t.Fatalf("无效编号未被拒绝: %q, %v", id, err)
		}
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := client.WebEpisodeComments(ctx, seriesID, episodeID); !errors.Is(err, context.Canceled) || calls != 0 {
		t.Fatalf("取消或无效请求仍访问了网络: calls=%d, %v", calls, err)
	}
	if _, err := client.WebEpisodeComments(context.Background(), seriesID, episodeID); !errors.Is(err, sdk.ErrTransport) || strings.Contains(err.Error(), "secret") {
		t.Fatalf("网络错误未脱敏: %v", err)
	}
}

func TestWebEpisodeCommentsSupportsBareAssignmentAndGzip(t *testing.T) {
	const seriesID, episodeID = "7681212396911741976", "7681231144708230168"
	body := bytes.Replace(commentsHTML(t, seriesID, episodeID, map[string]any{"total_count": 0, "reviews": []any{}}), []byte("window._ROUTER_DATA"), []byte("_ROUTER_DATA"), 1)
	var compressed bytes.Buffer
	writer := gzip.NewWriter(&compressed)
	if _, err := writer.Write(body); err != nil {
		t.Fatal(err)
	}
	if err := writer.Close(); err != nil {
		t.Fatal(err)
	}
	client := commentsClient(t, func(context.Context, *sdk.Request) (*sdk.Response, error) {
		return &sdk.Response{StatusCode: 200, Body: compressed.Bytes(), Headers: map[string]string{"Content-Encoding": "gzip"}}, nil
	})
	if page, err := client.WebEpisodeComments(context.Background(), seriesID, episodeID); err != nil || page.Total == nil || *page.Total != 0 {
		t.Fatalf("gzip公开网页解析失败: %+v, %v", page, err)
	}
}
