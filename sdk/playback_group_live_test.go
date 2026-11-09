//go:build live

package sdk_test

import (
	"bytes"
	"compress/gzip"
	"context"
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/beilunyang/openhongguo/adapters/filestore"
	"github.com/beilunyang/openhongguo/adapters/httptransport"
	"github.com/beilunyang/openhongguo/internal/drm"
	"github.com/beilunyang/openhongguo/internal/protocol"
	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

const liveSeriesID = "7697459724635098174"

type livePlaybackResponse struct {
	status   int
	encoding string
	body     []byte
}

type livePlaybackTransport struct {
	next      sdk.Transport
	responses []livePlaybackResponse
}

func (transport *livePlaybackTransport) Do(ctx context.Context, request *sdk.Request) (*sdk.Response, error) {
	response, err := transport.next.Do(ctx, request)
	if err != nil || response == nil || !strings.Contains(request.URL, protocol.PathPlayback) {
		return response, err
	}
	transport.responses = append(transport.responses, livePlaybackResponse{
		status:   response.StatusCode,
		encoding: response.Headers["Content-Encoding"],
		body:     bytes.Clone(response.Body),
	})
	return response, err
}

func TestLiveEpisode12PlaybackGroups(t *testing.T) {
	profileSource := os.Getenv("OHG_LIVE_PROFILE_SOURCE")
	if profileSource == "" {
		configDir, err := os.UserConfigDir()
		if err != nil {
			t.Fatal("cannot locate the profile directory")
		}
		profileSource = filepath.Join(configDir, "openhongguo", "anonymous.json")
	}
	profileCopy := filepath.Join(t.TempDir(), "anonymous.json")
	if saved, err := os.ReadFile(profileSource); err == nil {
		if err := os.WriteFile(profileCopy, saved, 0600); err != nil {
			t.Fatal("cannot create isolated profile")
		}
	} else if !errors.Is(err, os.ErrNotExist) {
		t.Fatal("cannot read source profile")
	}

	transport := &livePlaybackTransport{next: httptransport.New()}
	client, err := sdk.NewClient(sdk.Options{
		Transport: transport, ProfileStore: filestore.New(profileCopy), RetryAttempts: 2,
	})
	if err != nil {
		t.Fatal("cannot initialize diagnostic client")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 90*time.Second)
	defer cancel()

	details, err := client.GetSeriesBatch(ctx, []string{liveSeriesID})
	if err != nil || len(details) != 1 {
		t.Fatalf("series detail unavailable: %s", liveErrorKind(err))
	}
	var episodeID string
	for _, episode := range details[0].Episodes {
		if episode.Number == 12 {
			episodeID = episode.ID
			break
		}
	}
	if episodeID == "" {
		t.Fatal("episode 12 missing from series detail")
	}

	playback, playbackErr := client.ResolvePlayback(ctx, episodeID, sdk.PlaybackOptions{})
	t.Logf("episode=12 playback=%s model_present=%t responses=%d", liveErrorKind(playbackErr), playback != nil, len(transport.responses))
	if playback != nil {
		logLivePlaybackGroup(t, "selected", playback)
	}
	if len(transport.responses) == 0 {
		t.Fatal("playback request did not return an HTTP response")
	}
	for index, response := range transport.responses {
		t.Logf("response=%d http_status=%d", index+1, response.status)
		body, err := liveResponseBody(response)
		if err != nil {
			t.Logf("response=%d decode=failed", index+1)
			continue
		}
		for _, group := range []string{"ordinary", "dynamic"} {
			parsed, ok := livePlaybackGroup(body, episodeID, group)
			if !ok {
				t.Logf("response=%d group=%s parse=unavailable", index+1, group)
				continue
			}
			logLivePlaybackGroup(t, group, parsed)
		}
	}
}

func liveResponseBody(response livePlaybackResponse) ([]byte, error) {
	if !strings.EqualFold(response.encoding, "gzip") {
		return response.body, nil
	}
	reader, err := gzip.NewReader(bytes.NewReader(response.body))
	if err != nil {
		return nil, err
	}
	defer reader.Close()
	return io.ReadAll(reader)
}

func livePlaybackGroup(raw []byte, episodeID, group string) (*model.Playback, bool) {
	var envelope struct {
		Data struct {
			VideoModel json.RawMessage `json:"video_model"`
		} `json:"data"`
	}
	if json.Unmarshal(raw, &envelope) != nil {
		return nil, false
	}
	modelJSON := envelope.Data.VideoModel
	if len(modelJSON) > 0 && modelJSON[0] == '"' {
		var encoded string
		if json.Unmarshal(modelJSON, &encoded) != nil {
			return nil, false
		}
		modelJSON = []byte(encoded)
	}
	var fields map[string]json.RawMessage
	if json.Unmarshal(modelJSON, &fields) != nil {
		return nil, false
	}
	if group == "ordinary" {
		delete(fields, "dynamic_video")
	} else {
		delete(fields, "video_list")
	}
	modelJSON, err := json.Marshal(fields)
	if err != nil {
		return nil, false
	}
	wrapped, err := json.Marshal(map[string]any{"data": map[string]any{"video_model": json.RawMessage(modelJSON)}})
	if err != nil {
		return nil, false
	}
	playback, err := protocol.ParsePlayback(wrapped, episodeID)
	return playback, err == nil
}

var liveQualityLabel = regexp.MustCompile(`^[A-Za-z0-9_]{1,20}$`)

func logLivePlaybackGroup(t *testing.T, name string, playback *model.Playback) {
	labels := make([]string, 0, len(playback.Streams))
	for _, stream := range playback.Streams {
		label := stream.Definition
		if !liveQualityLabel.MatchString(label) {
			label = "other"
		}
		labels = append(labels, label)
	}
	_, deriveErr := drm.Derive(playback.KeyMaterial)
	t.Logf("group=%s streams=%d levels=%s protected=%t key_material_present=%t derive_success=%t audio_present=%t",
		name, len(playback.Streams), strings.Join(labels, ","), playback.Protected,
		playback.KeyMaterial != "", deriveErr == nil, playback.AudioURL != "")
}

func liveErrorKind(err error) string {
	switch {
	case err == nil:
		return "ok"
	case errors.Is(err, sdk.ErrDRMProtected):
		return "protected"
	case errors.Is(err, sdk.ErrAnonymousLimited):
		return "anonymous_limited"
	case errors.Is(err, sdk.ErrRateLimited):
		return "rate_limited"
	case errors.Is(err, sdk.ErrTransport):
		return "transport"
	case errors.Is(err, context.DeadlineExceeded):
		return "timeout"
	default:
		return "other"
	}
}
