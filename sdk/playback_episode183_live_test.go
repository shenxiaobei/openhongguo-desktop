//go:build live

package sdk_test

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
	"time"

	"github.com/beilunyang/openhongguo/adapters/filestore"
	"github.com/beilunyang/openhongguo/adapters/httptransport"
	"github.com/beilunyang/openhongguo/internal/drm"
	"github.com/beilunyang/openhongguo/sdk"
)

func TestLiveEpisode183PlaybackDiagnosis(t *testing.T) {
	for _, seriesID := range []string{"7703760419889876030", "7697459724635098174"} {
		t.Run(seriesID, func(t *testing.T) {
			// 用户档案只读取副本，所有匿名状态写入测试临时目录。
			configDir, err := os.UserConfigDir()
			if err != nil {
				t.Fatal("配置目录读取失败")
			}
			profileCopy := filepath.Join(t.TempDir(), "anonymous.json")
			if saved, err := os.ReadFile(filepath.Join(configDir, "openhongguo", "anonymous.json")); err == nil {
				if err := os.WriteFile(profileCopy, saved, 0600); err != nil {
					t.Fatal("隔离档案写入失败")
				}
			}
			transport := &livePlaybackTransport{next: httptransport.New()}
			client, err := sdk.NewClient(sdk.Options{Transport: transport, ProfileStore: filestore.New(profileCopy), RetryAttempts: 2})
			if err != nil {
				t.Fatal("诊断客户端初始化失败")
			}
			ctx, cancel := context.WithTimeout(context.Background(), 90*time.Second)
			defer cancel()
			details, err := client.GetSeriesBatch(ctx, []string{seriesID})
			if err != nil || len(details) != 1 {
				t.Fatalf("series=%s detail=%s", seriesID, liveErrorKind(err))
			}
			t.Logf("series=%s title=%s episode_count=%d", seriesID, details[0].Series.Title, len(details[0].Episodes))
			for _, number := range []int{183, 183, 184} {
				episodeID := ""
				for _, episode := range details[0].Episodes {
					if episode.Number == number {
						episodeID = episode.ID
						break
					}
				}
				if episodeID == "" {
					t.Logf("episode=%d missing=true", number)
					continue
				}
				before := len(transport.responses)
				playback, err := client.ResolvePlayback(ctx, episodeID, sdk.PlaybackOptions{})
				t.Logf("episode=%d id=%s resolve=%s model_present=%t responses=%d", number, episodeID, liveErrorKind(err), playback != nil, len(transport.responses)-before)
				if err != nil && !errors.Is(err, sdk.ErrDRMProtected) {
					continue
				}
				if playback != nil {
					logLivePlaybackGroup(t, "selected", playback)
				}
				for index, response := range transport.responses[before:] {
					body, decodeErr := liveResponseBody(response)
					t.Logf("episode=%d response=%d status=%d decoded=%t", number, index+1, response.status, decodeErr == nil)
					if decodeErr != nil {
						continue
					}
					for _, group := range []string{"ordinary", "dynamic"} {
						parsed, ok := livePlaybackGroup(body, episodeID, group)
						if ok {
							logLivePlaybackGroup(t, group, parsed)
						} else {
							t.Logf("group=%s parse_present=false", group)
						}
					}
					logLive183ProtectionShape(t, body)
				}
			}
		})
	}
}

func logLive183ProtectionShape(t *testing.T, body []byte) {
	var envelope struct {
		Data struct {
			VideoModel json.RawMessage `json:"video_model"`
		} `json:"data"`
	}
	if json.Unmarshal(body, &envelope) != nil {
		return
	}
	raw := bytes.TrimSpace(envelope.Data.VideoModel)
	if len(raw) > 0 && raw[0] == '"' {
		var encoded string
		if json.Unmarshal(raw, &encoded) != nil {
			return
		}
		raw = []byte(encoded)
	}
	var model struct {
		VideoList []json.RawMessage `json:"video_list"`
	}
	if json.Unmarshal(raw, &model) != nil {
		return
	}
	firstWrapped := ""
	for index, item := range model.VideoList {
		var fields map[string]json.RawMessage
		if json.Unmarshal(item, &fields) != nil {
			continue
		}
		var info struct {
			SpadeA  string `json:"spade_a"`
			Method  string `json:"encryption_method"`
			Encrypt bool   `json:"encrypt"`
		}
		_ = json.Unmarshal(fields["encrypt_info"], &info)
		var infoFields map[string]json.RawMessage
		_ = json.Unmarshal(fields["encrypt_info"], &infoFields)
		var outer string
		_ = json.Unmarshal(fields["spade_a"], &outer)
		var gear string
		_ = json.Unmarshal(fields["gear_des_key"], &gear)
		var outerMethod, outerKID string
		_ = json.Unmarshal(fields["encryption_method"], &outerMethod)
		_ = json.Unmarshal(fields["kid"], &outerKID)
		var innerKID string
		_ = json.Unmarshal(infoFields["kid"], &innerKID)
		wrapped := strings.TrimSpace(info.SpadeA)
		if wrapped == "" {
			wrapped = strings.TrimSpace(outer)
		}
		if index == 0 {
			firstWrapped = wrapped
		}
		_, deriveErr := drm.Derive(wrapped)
		method := "other"
		if info.Method == "" || info.Method == "cenc-aes-ctr" {
			method = info.Method
		}
		t.Logf("stream=%d nested_spade_present=%t outer_spade_present=%t wrapped_length=%d same_as_first=%t readable=%t encrypt=%t method=%s check_info_present=%t gear_present=%t gear_encryption_marker=%t", index,
			info.SpadeA != "", outer != "", len(wrapped), wrapped == firstWrapped, deriveErr == nil, info.Encrypt, method, len(fields["check_info"]) > 0, len(fields["gear_des_key"]) > 0, strings.Contains(gear, "encr"))
		if index == 0 {
			t.Logf("protection_shape encrypt_info=%s outer_encrypt=%s inner_encrypt=%s outer_method_present=%t outer_kid_present=%t inner_kid_present=%t check_info=%s gear_tokens=%s",
				live183Shape(fields["encrypt_info"]), live183Bool(fields["encrypt"]), live183Bool(infoFields["encrypt"]), outerMethod != "", outerKID != "", innerKID != "", live183Shape(fields["check_info"]), live183Gear(gear))
		}
	}
}

func live183Bool(raw json.RawMessage) string {
	trimmed := strings.TrimSpace(string(raw))
	switch trimmed {
	case "", "null", "true", "false", "0", "1":
		return trimmed
	default:
		return "other"
	}
}

func live183Shape(raw json.RawMessage) string {
	var value any
	if len(raw) == 0 {
		return "missing"
	}
	if json.Unmarshal(raw, &value) != nil {
		return "invalid"
	}
	switch value := value.(type) {
	case nil:
		return "null"
	case string:
		return "string"
	case bool:
		return "bool"
	case float64:
		return "number"
	case []any:
		return "array"
	case map[string]any:
		fields := []string{}
		for key, item := range value {
			if !liveQualityLabel.MatchString(key) {
				continue
			}
			encoded, _ := json.Marshal(item)
			fields = append(fields, key+":"+live183Shape(encoded))
		}
		sort.Strings(fields)
		return "object{" + strings.Join(fields, ",") + "}"
	default:
		return "other"
	}
}

func live183Gear(gear string) string {
	// 只保留容器、加密枚举、编码与清晰度，其他段不输出。
	parts := []string{}
	for _, part := range strings.Split(gear, "|") {
		pair := strings.SplitN(part, ":", 2)
		if len(pair) != 2 || !liveQualityLabel.MatchString(pair[1]) {
			continue
		}
		switch pair[0] {
		case "0", "1", "2", "4":
			parts = append(parts, part)
		}
	}
	return strings.Join(parts, "|")
}
