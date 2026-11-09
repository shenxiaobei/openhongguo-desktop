package sdk_test

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/beilunyang/openhongguo/adapters/httptransport"
	"github.com/beilunyang/openhongguo/adapters/memstore"
	"github.com/beilunyang/openhongguo/internal/protocol"
	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

const (
	danmakuSeriesID  = "7100000000000101"
	danmakuEpisodeID = "7100000000000203"
)

func newDanmakuTestClient(t *testing.T, handler http.Handler) (*sdk.Client, *logCapture) {
	t.Helper()
	server := httptest.NewServer(handler)
	t.Cleanup(server.Close)
	store := memstore.New()
	device := model.DefaultDevice
	if err := store.Save(context.Background(), &sdk.DeviceProfile{
		DeviceID: "fixture-sensitive-device", InstallID: "fixture-sensitive-install", TTReq: "fixture-sensitive-ticket", Device: &device,
	}); err != nil {
		t.Fatal(err)
	}
	logs := &logCapture{}
	client, err := sdk.NewClient(sdk.Options{
		Transport: httptransport.New(), ProfileStore: store, APIOrigin: server.URL, RegisterOrigin: server.URL,
		RetryAttempts: 1, DeviceResets: sdk.Int(0), Logger: logs,
		Clock: func() time.Time { return time.UnixMilli(1_700_000_000_123).UTC() },
	})
	if err != nil {
		t.Fatal(err)
	}
	return client, logs
}

func danmakuTestRow(id, group, text string, offset any) map[string]any {
	return map[string]any{"comment": map[string]any{
		"comment_id": id,
		"common":     map[string]any{"group_id": group, "status": 1, "content": map[string]any{"text": text}},
		"expand":     map[string]any{"offset_time": offset},
	}}
}

func danmakuTestPage(t *testing.T, writer http.ResponseWriter, rows []any, extra, info map[string]any) {
	t.Helper()
	writer.Header().Set("Content-Type", "application/json")
	if err := json.NewEncoder(writer).Encode(map[string]any{"code": 0, "data": map[string]any{
		"data_list": rows, "extra": extra, "common_list_info": info,
	}}); err != nil {
		t.Error("fixture response encoding failed")
	}
}

func TestDanmakuSignedRequestAndTwoPageProgression(t *testing.T) {
	requests := 0
	client, logs := newDanmakuTestClient(t, http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		requests++
		if request.Method != http.MethodPost || request.URL.Path != "/novel/commentapi/comment/list/"+danmakuEpisodeID+"/v1/" {
			t.Errorf("unexpected method or path")
		}
		body, err := io.ReadAll(request.Body)
		if err != nil {
			t.Error("fixture request reading failed")
			return
		}
		for _, name := range []string{"X-Gorgon", "X-Helios", "X-Medusa", "X-Khronos"} {
			if request.Header.Get(name) == "" {
				t.Errorf("missing signature header %s", name)
			}
		}
		if request.Header.Get("X-SS-STUB") != protocol.ContentMD5(body) || request.Header.Get("Comment-Source") != "601" || request.Header.Get("Server-Channel") != "1000" {
			t.Error("body digest or comment headers differ")
		}
		if request.URL.Query().Get("device_id") != "fixture-sensitive-device" || request.URL.Query().Get("aid") != protocol.AID || !strings.Contains(request.Header.Get("Cookie"), "fixture-sensitive-ticket") {
			t.Error("request did not reuse existing anonymous identity")
		}
		var payload struct {
			CommentSource int    `json:"comment_source"`
			ServerChannel int    `json:"server_channel"`
			GroupID       string `json:"group_id"`
			GroupType     int    `json:"group_type"`
			CommentType   int    `json:"comment_type"`
			Sort          int    `json:"sort"`
			Count         int    `json:"count"`
			Cursor        string `json:"cursor"`
			AID           int    `json:"aid"`
			Business      struct {
				BookID   string `json:"book_id"`
				Start    int64  `json:"start_offset_time"`
				Duration int64  `json:"playlet_item_duration"`
				Guide    []int  `json:"need_danmaku_guide_type"`
			} `json:"business_param"`
		}
		if json.Unmarshal(body, &payload) != nil {
			t.Error("fixture request is not JSON")
			return
		}
		if payload.CommentSource != 601 || payload.ServerChannel != 1000 || payload.GroupID != danmakuEpisodeID || payload.GroupType != 30 || payload.CommentType != 20 || payload.Sort != 1 || payload.Count != 90 || payload.Cursor != "" || payload.AID != 8662 || payload.Business.BookID != danmakuSeriesID || payload.Business.Duration != 90000 || fmt.Sprint(payload.Business.Guide) != "[1 3 4 2]" {
			t.Error("comment request contract differs")
		}
		if requests == 1 {
			if payload.Business.Start != 0 {
				t.Errorf("first start = %d", payload.Business.Start)
			}
			danmakuTestPage(t, writer, []any{danmakuTestRow("1001", danmakuEpisodeID, "第一条", 902)}, map[string]any{"next_query_danmaku_list_time": "30000"}, map[string]any{"has_more": true, "total": "2"})
			return
		}
		if requests != 2 || payload.Business.Start != 30000 {
			t.Errorf("unexpected extra request or second start = %d", payload.Business.Start)
		}
		danmakuTestPage(t, writer, []any{danmakuTestRow("1002", danmakuEpisodeID, "第二条", "30234")}, nil, map[string]any{"has_more": false, "cursor": `{"end_offset_time":60000}`})
	}))
	first, err := client.Danmaku(context.Background(), danmakuSeriesID, danmakuEpisodeID, sdk.DanmakuOptions{DurationMS: 90000})
	if err != nil {
		t.Fatal(err)
	}
	if requests != 1 || len(first.Items) != 1 || *first.Items[0].PositionMS != 902 || first.Cursor != "30000" || !first.HasMore || first.Total == nil || *first.Total != 2 {
		t.Fatalf("first page shape or milliseconds differ: %+v", first)
	}
	second, err := client.Danmaku(context.Background(), danmakuSeriesID, danmakuEpisodeID, sdk.DanmakuOptions{DurationMS: 90000, Cursor: first.Cursor})
	if err != nil {
		t.Fatal(err)
	}
	if requests != 2 || len(second.Items) != 1 || *second.Items[0].PositionMS != 30234 || second.HasMore || second.Cursor != "" {
		t.Fatalf("second page or explicit end differs: %+v", second)
	}
	for _, line := range logs.lines {
		for _, secret := range []string{"fixture-sensitive-device", "fixture-sensitive-install", "fixture-sensitive-ticket", "X-Medusa", "第一条", "第二条"} {
			if strings.Contains(line, secret) {
				t.Error("diagnostic log contains request or content material")
			}
		}
	}
}

func TestDanmakuFiltersAndDeduplicatesTimeline(t *testing.T) {
	rows := []any{
		danmakuTestRow("1001", danmakuEpisodeID, "same", "2017"),
		danmakuTestRow("1002", danmakuEpisodeID, "same", 2017),
		danmakuTestRow("1001", danmakuEpisodeID, "same ID with changed text", 3011),
		danmakuTestRow("1003", "7100000000000999", "other episode", 1000),
		danmakuTestRow("1004", danmakuEpisodeID, "fraction", 1.25),
		danmakuTestRow("1005", danmakuEpisodeID, "missing", nil),
		danmakuTestRow("1006", danmakuEpisodeID, "negative", -1),
		danmakuTestRow("1007", danmakuEpisodeID, "past duration", 90001),
		danmakuTestRow("1008", danmakuEpisodeID, " ", 3000),
		danmakuTestRow("1009", danmakuEpisodeID, "earlier", 0),
		map[string]any{"comment": map[string]any{"comment_id": "1010", "common": map[string]any{"group_id": danmakuEpisodeID, "status": "1", "content": map[string]any{"text": "fallback"}}, "offset_time": "3001"}},
		map[string]any{"comment": map[string]any{"comment_id": "1011", "common": map[string]any{"group_id": danmakuEpisodeID, "status": 0, "content": map[string]any{"text": "hidden"}}, "expand": map[string]any{"offset_time": 1000}}},
		map[string]any{"comment": map[string]any{"comment_id": "1012", "common": map[string]any{"group_id": danmakuEpisodeID, "content": map[string]any{"text": "missing status"}}, "expand": map[string]any{"offset_time": 1000}}},
		"malformed row",
	}
	client, _ := newDanmakuTestClient(t, http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		danmakuTestPage(t, writer, rows, nil, map[string]any{"has_more": false})
	}))
	page, err := client.Danmaku(context.Background(), danmakuSeriesID, danmakuEpisodeID, sdk.DanmakuOptions{DurationMS: 90000})
	if err != nil {
		t.Fatal(err)
	}
	if len(page.Items) != 3 || *page.Items[0].PositionMS != 0 || *page.Items[1].PositionMS != 2017 || *page.Items[2].PositionMS != 3001 {
		t.Fatalf("filtered timeline = %+v", page.Items)
	}
}

func TestDanmakuInvalidRowsDoNotLookLikeValidEmptyPage(t *testing.T) {
	client, _ := newDanmakuTestClient(t, http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		danmakuTestPage(t, writer, []any{
			danmakuTestRow("1001", "7100000000000999", "wrong episode", 1000),
			danmakuTestRow("1002", danmakuEpisodeID, "missing offset", nil),
			danmakuTestRow("1003", danmakuEpisodeID, "", 2000),
		}, nil, map[string]any{"has_more": false})
	}))
	page, err := client.Danmaku(context.Background(), danmakuSeriesID, danmakuEpisodeID, sdk.DanmakuOptions{DurationMS: 90000})
	if err != nil || page == nil || len(page.Items) != 0 || !page.SourceLimited || page.Note == "" || page.HasMore {
		t.Fatalf("invalid rows looked like an ordinary empty page: %+v, %v", page, err)
	}
}

func TestDanmakuPaginationSourcesAndStagnation(t *testing.T) {
	tests := []struct {
		name    string
		extra   map[string]any
		info    map[string]any
		cursor  string
		limited bool
	}{
		{"numeric extra", map[string]any{"next_query_danmaku_list_time": 30000}, nil, "30000", false},
		{"string extra", map[string]any{"next_query_danmaku_list_time": "30000"}, nil, "30000", false},
		{"cursor end offset", nil, map[string]any{"has_more": true, "cursor": `{"end_offset_time":"30000","danmaku_count":90}`}, "30000", false},
		{"cursor next offset object", nil, map[string]any{"has_more": 1, "cursor": map[string]any{"next_offset_time": 30000}}, "30000", false},
		{"cursor offset", nil, map[string]any{"has_more": "1", "cursor": `{"offset":30000}`}, "30000", false},
		{"earliest advancing source", map[string]any{"next_query_danmaku_list_time": 60000}, map[string]any{"has_more": true, "cursor": `{"end_offset_time":30000}`}, "30000", false},
		{"fallback from stale extra", map[string]any{"next_query_danmaku_list_time": 1000}, map[string]any{"has_more": true, "cursor": `{"end_offset_time":30000}`}, "30000", false},
		{"explicit false wins", map[string]any{"next_query_danmaku_list_time": 30000}, map[string]any{"has_more": false}, "", false},
		{"explicit zero wins", map[string]any{"next_query_danmaku_list_time": 30000}, map[string]any{"has_more": "0"}, "", false},
		{"duration end", map[string]any{"next_query_danmaku_list_time": 90000}, map[string]any{"has_more": true}, "", false},
		{"stagnant", map[string]any{"next_query_danmaku_list_time": 1000}, map[string]any{"has_more": true}, "", true},
		{"backwards", map[string]any{"next_query_danmaku_list_time": 999}, map[string]any{"has_more": true}, "", true},
		{"out of duration", map[string]any{"next_query_danmaku_list_time": 90001}, map[string]any{"has_more": true}, "", true},
		{"missing continuation", nil, map[string]any{"has_more": true}, "", true},
		{"missing all pagination metadata", nil, nil, "", true},
		{"malformed continuation", nil, map[string]any{"has_more": true, "cursor": `{"offset":"2e4"}`}, "", true},
		{"untrusted cursor fields", nil, map[string]any{"has_more": true, "cursor": `{"url":"https://example.invalid/private","offset":null}`}, "", true},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			client, _ := newDanmakuTestClient(t, http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
				danmakuTestPage(t, writer, []any{danmakuTestRow("1001", danmakuEpisodeID, "retained", 1234), danmakuTestRow("1002", danmakuEpisodeID, "old duplicate", 999)}, test.extra, test.info)
			}))
			page, err := client.Danmaku(context.Background(), danmakuSeriesID, danmakuEpisodeID, sdk.DanmakuOptions{DurationMS: 90000, Cursor: "001000"})
			if err != nil {
				t.Fatal(err)
			}
			if len(page.Items) != 1 || *page.Items[0].PositionMS != 1234 || page.Cursor != test.cursor || page.HasMore != (test.cursor != "") || page.SourceLimited != test.limited || (test.limited && page.Note == "") {
				t.Fatalf("unexpected pagination result: %+v", page)
			}
		})
	}
}

func TestDanmakuRejectsInvalidOptionsBeforeNetwork(t *testing.T) {
	var requests atomic.Int32
	client, _ := newDanmakuTestClient(t, http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		requests.Add(1)
		writer.WriteHeader(http.StatusInternalServerError)
	}))
	for _, cursor := range []string{"-1", "+1", "1.0", "1e3", " 1", "1 ", "null", "90000", "90001", "9223372036854775808", `{"end_offset_time":1}`, "١"} {
		_, err := client.Danmaku(context.Background(), danmakuSeriesID, danmakuEpisodeID, sdk.DanmakuOptions{DurationMS: 90000, Cursor: cursor})
		if !errors.Is(err, sdk.ErrInvalidOptions) {
			t.Errorf("cursor should match invalid options, got %v", err)
		}
	}
	for _, duration := range []int64{-1, 0, 86400001} {
		_, err := client.Danmaku(context.Background(), danmakuSeriesID, danmakuEpisodeID, sdk.DanmakuOptions{DurationMS: duration})
		if !errors.Is(err, sdk.ErrInvalidOptions) {
			t.Errorf("duration %d should match invalid options", duration)
		}
	}
	for _, ids := range [][2]string{{"1", danmakuEpisodeID}, {danmakuSeriesID, "episode/../../private"}} {
		_, err := client.Danmaku(context.Background(), ids[0], ids[1], sdk.DanmakuOptions{DurationMS: 90000})
		if !errors.Is(err, sdk.ErrInvalidOptions) {
			t.Error("invalid ID accepted")
		}
	}
	if requests.Load() != 0 {
		t.Fatal("invalid input performed a request")
	}
}

func TestDanmakuResponseShapeAndFailures(t *testing.T) {
	tests := []struct {
		name    string
		body    string
		status  int
		valid   bool
		limited bool
		kind    error
	}{
		{"valid empty", `{"code":0,"data":{"data_list":[],"common_list_info":{"has_more":false}}}`, 200, true, false, nil},
		{"valid empty without metadata", `{"code":"0","data":{"data_list":[]}}`, 200, true, true, nil},
		{"missing list", `{"code":0,"data":{}}`, 200, false, false, nil},
		{"null list", `{"code":0,"data":{"data_list":null}}`, 200, false, false, nil},
		{"object list", `{"code":0,"data":{"data_list":{}}}`, 200, false, false, nil},
		{"missing data", `{"code":0}`, 200, false, false, nil},
		{"null data", `{"code":0,"data":null}`, 200, false, false, nil},
		{"missing status", `{"data":{"data_list":[]}}`, 200, false, false, nil},
		{"html", `<html>fixture-sensitive-response</html>`, 200, false, false, nil},
		{"broken JSON", `{"code":0,"data":{"data_list":[`, 200, false, false, nil},
		{"business failure", `{"code":100103,"message":"fixture-sensitive-response https://private.invalid/?ticket=secret"}`, 200, false, false, sdk.ErrParamInvalid},
		{"authentication failure", `{"code":0,"data":{"data_list":[]}}`, 403, false, false, sdk.ErrAuthenticationRequired},
		{"rate limit", `{}`, 429, false, false, sdk.ErrRateLimited},
		{"server failure", `fixture-sensitive-response`, 500, false, false, nil},
		{"empty response", ``, 200, false, false, sdk.ErrAnonymousLimited},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			client, _ := newDanmakuTestClient(t, http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
				writer.WriteHeader(test.status)
				_, _ = io.WriteString(writer, test.body)
			}))
			page, err := client.Danmaku(context.Background(), danmakuSeriesID, danmakuEpisodeID, sdk.DanmakuOptions{DurationMS: 90000})
			if test.valid {
				if err != nil || page == nil || page.Items == nil || len(page.Items) != 0 || page.HasMore || page.SourceLimited != test.limited {
					t.Fatalf("valid empty page = %+v, %v", page, err)
				}
				if test.limited && page.Note != "来源未返回后续时间段信息，当前内容已保留。" {
					t.Fatalf("missing pagination metadata was not explained: %+v", page)
				}
				return
			}
			if err == nil || page != nil || (test.kind != nil && !errors.Is(err, test.kind)) {
				t.Fatalf("failure = %+v, %v", page, err)
			}
			for _, secret := range []string{"fixture-sensitive-response", "private.invalid", "ticket=secret", "fixture-sensitive-device"} {
				if strings.Contains(err.Error(), secret) {
					t.Error("error exposed request/response material")
				}
			}
		})
	}
}

func TestDanmakuCancellationBeforeAndDuringRequest(t *testing.T) {
	entered := make(chan struct{}, 1)
	release := make(chan struct{})
	defer close(release)
	client, _ := newDanmakuTestClient(t, http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		entered <- struct{}{}
		select {
		case <-request.Context().Done():
		case <-release:
		}
	}))
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := client.Danmaku(ctx, danmakuSeriesID, danmakuEpisodeID, sdk.DanmakuOptions{DurationMS: 90000}); !errors.Is(err, context.Canceled) {
		t.Fatalf("pre-canceled result = %v", err)
	}
	select {
	case <-entered:
		t.Fatal("pre-canceled call made a request")
	default:
	}
	ctx, cancel = context.WithCancel(context.Background())
	defer cancel()
	finished := make(chan error, 1)
	go func() {
		_, err := client.Danmaku(ctx, danmakuSeriesID, danmakuEpisodeID, sdk.DanmakuOptions{DurationMS: 90000})
		finished <- err
	}()
	select {
	case <-entered:
	case <-time.After(3 * time.Second):
		t.Fatal("request did not start")
	}
	cancel()
	select {
	case err := <-finished:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("in-flight cancellation = %v", err)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("request did not stop after cancellation")
	}
}
