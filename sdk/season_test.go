package sdk_test

import (
	"context"
	"errors"
	"net/http"
	"testing"

	"github.com/beilunyang/openhongguo/internal/protocol"
	"github.com/beilunyang/openhongguo/sdk"
)

func TestSeriesSeasonsUsesOnlyOfficialRowAndPreservesVerificationErrors(t *testing.T) {
	for _, test := range []struct {
		name   string
		status int
		body   string
		want   error
	}{
		{name: "官方系列行"},
		{name: "空行", body: `{"code":0,"data":[]}`},
		{name: "认证", status: 403, want: sdk.ErrAuthenticationRequired},
		{name: "限流", status: 429, want: sdk.ErrRateLimited},
		{name: "验证", body: `{"code":111,"message":"HIT_VERIFY_CODE"}`, want: sdk.ErrChallengeRequired},
	} {
		t.Run(test.name, func(t *testing.T) {
			client, _ := testClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				switch r.URL.Path {
				case protocol.PathRegister:
					_, _ = w.Write(fixture(t, "register-response.json"))
				case protocol.PathPlan:
					if r.URL.Query().Get("from") != protocol.PlanFromSeriesSeasons || r.URL.Query().Get("book_id") != "7100000000000101" {
						t.Error("没有查询当前作品的官方系列剧行")
					}
					if test.status != 0 {
						w.WriteHeader(test.status)
					}
					if test.body != "" {
						_, _ = w.Write([]byte(test.body))
					} else if test.status == 0 {
						_, _ = w.Write(fixture(t, "series-seasons.json"))
					}
				default:
					t.Errorf("严格系列读取请求了备用接口：%s", r.URL.Path)
					http.NotFound(w, r)
				}
			}))
			rows, err := client.SeriesSeasons(context.Background(), "7100000000000101")
			if test.want != nil {
				if !errors.Is(err, test.want) {
					t.Fatalf("错误被搜索或空结果吞掉：%v", err)
				}
				return
			}
			if err != nil || test.name == "官方系列行" && len(rows) != 3 || test.name == "空行" && len(rows) != 0 {
				t.Fatalf("官方系列结果不正确：%+v %v", rows, err)
			}
		})
	}
}
