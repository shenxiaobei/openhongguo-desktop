package model

import (
	"fmt"
	"sort"
	"strings"
)

// Playback 是一个已解析的匿名媒体模型。它描述服务端提供的内容，
// 从不携带解密密钥或许可证材料。
type Playback struct {
	EpisodeID string `json:"episode_id"`
	// VideoID 是分集背后的媒体 id。
	VideoID string `json:"video_id,omitempty"`
	// MediaType 是服务端的媒体类型，例如 "video"。
	MediaType       string `json:"media_type,omitempty"`
	DurationSeconds int    `json:"duration_seconds,omitempty"`
	// DurationMS 保留源模型的小数秒精度，供弹幕等时间轴功能使用。
	DurationMS int64 `json:"duration_ms,omitempty"`
	// ExpiresAt 是流 URL 失效的时间，单位是 unix 秒。
	ExpiresAt int64 `json:"expires_at,omitempty"`
	// Protected 标记加密的分集；参见 Stream.Protected。
	Protected bool     `json:"protected"`
	Streams   []Stream `json:"streams"`
	// AudioURL 是分段（DASH）作品（如长片电影）的独立音轨。
	// 单文件的表示形式留空。
	AudioURL string `json:"audio_url,omitempty"`
	// KeyMaterial 是服务端包装过的内容密钥（encrypt_info.spade_a）。
	// 它是不透明的 DRM 材料：从不序列化、不写日志、不复制进流，
	// 只有解密路径读取它。
	KeyMaterial string `json:"-"`
}

// ContentKey 是受保护播放模型的 CENC（cenc-aes-ctr）内容密钥，
// 由 sdk.ContentKey 还原。
type ContentKey struct {
	// Hex 是 16 字节 AES 密钥的小写十六进制形式，
	// 即 ffmpeg 的 -decryption_key 和播放器的 ClearKey 配置接受的形式。
	Hex string `json:"key"`
	// Version 是播放器在密钥旁读取的方案标记。
	Version string `json:"version,omitempty"`
}

// Stream 是媒体模型中的一个表示形式。Protected
// 标记官方客户端在内部解密的表示形式；sdk.ContentKey 从模型的 KeyMaterial
// 还原它的密钥。
type Stream struct {
	// Definition 是分辨率标签（"720p"）；Quality 是服务端的画质标签
	// （"normal"）。
	Definition string `json:"definition,omitempty"`
	Quality    string `json:"quality,omitempty"`
	Codec      string `json:"codec,omitempty"`
	Width      int    `json:"width,omitempty"`
	Height     int    `json:"height,omitempty"`
	// Bitrate 的单位是 bit/s；Size 是文件大小，单位是字节。
	Bitrate int64 `json:"bitrate,omitempty"`
	Size    int64 `json:"size,omitempty"`
	// BackupURL 是同一文件的第二个 CDN 地址。
	URL       string `json:"url,omitempty"`
	BackupURL string `json:"backup_url,omitempty"`
	// HLS 标记 m3u8 播放列表；Dash 标记分段（DASH）的表示形式。
	HLS       bool `json:"hls,omitempty"`
	Dash      bool `json:"dash,omitempty"`
	Protected bool `json:"protected"`
	// Encryption 是受保护流的加密方案，例如 "cenc-aes-ctr"。
	Encryption string `json:"encryption,omitempty"`
}

// Select 选出一个可播放的表示形式。quality 为空时，先选最高分辨率，
// 再选最高码率。"worst" 选最低的。其他值匹配分辨率标签（"720p"）、
// 画质标签（"normal"）或编码，并按相同顺序选出匹配流。没有 URL 的流永远不会被选中。
func (p *Playback) Select(quality string) (Stream, error) {
	if p == nil {
		return Stream{}, fmt.Errorf("playback model is missing")
	}
	usable := make([]Stream, 0, len(p.Streams))
	for _, s := range p.Streams {
		if strings.TrimSpace(s.URL) != "" {
			usable = append(usable, s)
		}
	}
	if len(usable) == 0 {
		return Stream{}, fmt.Errorf("playback model has no stream URL")
	}
	sort.SliceStable(usable, func(i, j int) bool {
		hi, hj := heightRank(usable[i]), heightRank(usable[j])
		if hi != hj {
			return hi < hj
		}
		return usable[i].Bitrate < usable[j].Bitrate
	})
	want := strings.ToLower(strings.TrimSpace(quality))
	switch want {
	case "", "best":
		return usable[len(usable)-1], nil
	case "worst":
		return usable[0], nil
	}
	// 从高到低匹配，避免手动选择同档位时反而取到最低码率。
	for i := len(usable) - 1; i >= 0; i-- {
		s := usable[i]
		if strings.EqualFold(s.Definition, want) || strings.EqualFold(s.Quality, want) || strings.EqualFold(s.Codec, want) {
			return s, nil
		}
	}
	return Stream{}, fmt.Errorf("quality %q is not offered (available: %s)", quality, strings.Join(definitions(usable), " "))
}

// heightRank 优先按完整尺寸的短边排序，避免把竖屏的长边当成更高画质。
// 尺寸缺失时兼容原有高度、分辨率标签和宽度回退。
func heightRank(s Stream) int {
	if s.Width > 0 && s.Height > 0 {
		return min(s.Width, s.Height)
	}
	if s.Height > 0 {
		return s.Height
	}
	digits := 0
	for _, r := range s.Definition {
		if r < '0' || r > '9' {
			break
		}
		digits = digits*10 + int(r-'0')
	}
	if digits > 0 {
		return digits
	}
	return s.Width
}

func definitions(streams []Stream) []string {
	out := make([]string, 0, len(streams))
	for _, s := range streams {
		switch {
		case s.Definition != "":
			out = append(out, s.Definition)
		case s.Quality != "":
			out = append(out, s.Quality)
		default:
			out = append(out, "unknown")
		}
	}
	return out
}
