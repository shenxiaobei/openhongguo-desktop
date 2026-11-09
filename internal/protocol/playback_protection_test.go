package protocol

import (
	"fmt"
	"testing"
)

func TestParsePlaybackSeparatesEncryptionFromDescriptiveMetadata(t *testing.T) {
	tests := []struct {
		name      string
		fields    string
		protected bool
		method    string
		key       string
	}{
		// 真实明文响应没有 encrypt 字段；档位描述与普通校验信息不代表加密。
		{name: "absent_flags_with_normal_gear_and_checksum", fields: `"gear_des_key":"0:MP4|1:normal|2:h265_hvc1|4:1080p","check_info":{"check_info":"fixture-checksum"}`},
		{name: "normal_gear_only", fields: `"gear_des_key":"0:MP4|1:normal|2:h265_hvc1|4:1080p"`},
		{name: "checksum_only", fields: `"check_info":{"check_info":"fixture-checksum"}`},
		{name: "unknown_gear_only", fields: `"gear_des_key":"fixture-descriptor"`},
		{name: "non_protection_gear_token", fields: `"gear_des_key":"0:MP4|1:normal|2:encrypted_codec|4:1080p"`},
		{name: "empty_encrypt_info", fields: `"encrypt_info":{},"check_info":{"check_info":"fixture-checksum"}`},
		{name: "null_encrypt_info", fields: `"encrypt_info":null,"gear_des_key":"0:MP4|1:normal"`},
		{name: "outer_false", fields: `"encrypt":false,"check_info":{"check_info":"fixture-checksum"}`},
		{name: "outer_zero", fields: `"encrypt":0,"gear_des_key":"0:MP4|1:normal"`},
		{name: "outer_false_string", fields: `"encrypt":"false","gear_des_key":"0:MP4|1:normal"`},
		{name: "outer_zero_string", fields: `"encrypt":"0","check_info":{"check_info":"fixture-checksum"}`},
		{name: "inner_false", fields: `"encrypt_info":{"encrypt":false},"gear_des_key":"0:MP4|1:normal"`},
		{name: "inner_zero", fields: `"encrypt_info":{"encrypt":0},"check_info":{"check_info":"fixture-checksum"}`},
		{name: "inner_false_string", fields: `"encrypt_info":{"encrypt":"false"},"gear_des_key":"0:MP4|1:normal"`},
		{name: "inner_zero_string", fields: `"encrypt_info":{"encrypt":"0"},"check_info":{"check_info":"fixture-checksum"}`},
		{name: "scalar_encrypt_info_false", fields: `"encrypt_info":false,"check_info":{"check_info":"fixture-checksum"}`},
		{name: "scalar_encrypt_info_false_string", fields: `"encrypt_info":"false","gear_des_key":"0:MP4|1:normal"`},
		{name: "scalar_encrypt_info_zero", fields: `"encrypt_info":0,"check_info":{"check_info":"fixture-checksum"}`},
		{name: "whitespace_only_material", fields: `"encrypt_info":{"encrypt":false,"kid":"  ","spade_a":"  "},"gear_des_key":"0:MP4|1:normal"`},
		{name: "outer_true", fields: `"encrypt":true`, protected: true},
		{name: "outer_one", fields: `"encrypt":1`, protected: true},
		{name: "outer_true_string", fields: `"encrypt":"true"`, protected: true},
		{name: "inner_true", fields: `"encrypt_info":{"encrypt":true}`, protected: true},
		{name: "inner_one", fields: `"encrypt_info":{"encrypt":1}`, protected: true},
		{name: "inner_true_string", fields: `"encrypt_info":{"encrypt":"true"}`, protected: true},
		{name: "outer_method", fields: `"encryption_method":"cenc-aes-ctr"`, protected: true, method: "cenc-aes-ctr"},
		{name: "inner_method", fields: `"encrypt_info":{"encryption_method":"cenc-aes-ctr"}`, protected: true, method: "cenc-aes-ctr"},
		{name: "outer_kid", fields: `"kid":"fixture-kid"`, protected: true},
		{name: "inner_kid", fields: `"encrypt_info":{"kid":"fixture-kid"}`, protected: true},
		{name: "outer_spade", fields: `"spade_a":"fixture-key"`, protected: true, key: "fixture-key"},
		{name: "inner_spade", fields: `"encrypt_info":{"spade_a":"fixture-key"}`, protected: true, key: "fixture-key"},
		{name: "gear_encr", fields: `"gear_des_key":"0:MP4|1:encr|2:h265_hvc1|4:1080p"`, protected: true},
		{name: "gear_encrypt", fields: `"gear_des_key":"0:DASH|1:encrypt|2:h265_hvc1|4:1080p"`, protected: true},
		{name: "gear_encrypted", fields: `"gear_des_key":"0:MP4|1:encrypted|2:h265_hvc1|4:1080p"`, protected: true},
		{name: "gear_encryption_case_and_whitespace", fields: `"gear_des_key":"0:MP4| 1 : ENCR |2:h265_hvc1|4:1080p"`, protected: true},
		// 矛盾响应保留强加密标志，避免 false 掩盖实际加密流。
		{name: "outer_false_inner_true", fields: `"encrypt":false,"encrypt_info":{"encrypt":true}`, protected: true},
		{name: "inner_false_outer_true", fields: `"encrypt":true,"encrypt_info":{"encrypt":false}`, protected: true},
		{name: "false_with_method", fields: `"encrypt_info":{"encrypt":false,"encryption_method":"cenc-aes-ctr"}`, protected: true, method: "cenc-aes-ctr"},
		{name: "false_with_kid", fields: `"encrypt":false,"kid":"fixture-kid"`, protected: true},
		{name: "false_with_spade", fields: `"encrypt_info":{"encrypt":false,"spade_a":"fixture-key"}`, protected: true, key: "fixture-key"},
		{name: "false_with_encrypted_gear", fields: `"encrypt_info":{"encrypt":false},"gear_des_key":"0:MP4|1:encr"`, protected: true},
		{name: "normal_gear_with_spade", fields: `"gear_des_key":"0:MP4|1:normal","spade_a":"fixture-key"`, protected: true, key: "fixture-key"},
		{name: "opaque_encrypt_info", fields: `"encrypt_info":"protected"`, protected: true},
		{name: "malformed_encrypt_info_field", fields: `"encrypt_info":{"encrypt":false,"kid":42}`, protected: true},
		{name: "unknown_encrypt_flag", fields: `"encrypt":"unknown-protection"`, protected: true},
	}
	for _, group := range []string{"ordinary", "dynamic"} {
		t.Run(group, func(t *testing.T) {
			for _, tt := range tests {
				t.Run(tt.name, func(t *testing.T) {
					item := fmt.Sprintf(`{"main_url":"https://media.invalid/video.mp4","video_meta":{"definition":"1080p","vwidth":1920,"vheight":1080,"bitrate":1000000},%s}`, tt.fields)
					videoModel := fmt.Sprintf(`{"video_list":[%s]}`, item)
					if group == "dynamic" {
						videoModel = fmt.Sprintf(`{"dynamic_video":{"dynamic_video_list":[%s],"dynamic_audio_list":[{"main_url":"https://media.invalid/audio.mp4"}]}}`, item)
					}
					raw := []byte(fmt.Sprintf(`{"code":0,"data":{"video_model":%s}}`, videoModel))
					playback, err := ParsePlayback(raw, "7100000000000401")
					if err != nil {
						t.Fatal(err)
					}
					stream, err := playback.Select("best")
					if err != nil {
						t.Fatal(err)
					}
					if playback.Protected != tt.protected || stream.Protected != tt.protected {
						t.Fatalf("加密状态不符：model=%t stream=%t want=%t", playback.Protected, stream.Protected, tt.protected)
					}
					if stream.Encryption != tt.method {
						t.Fatalf("加密方法被改变：got=%q want=%q", stream.Encryption, tt.method)
					}
					if playback.KeyMaterial != tt.key {
						t.Fatal("包装材料的保留行为被改变")
					}
					if group == "dynamic" && playback.AudioURL != "https://media.invalid/audio.mp4" {
						t.Fatal("加密状态解析丢失了动态音轨")
					}
				})
			}
		})
	}
}

func TestParsePlaybackHigherClearGroupKeepsItsMetadata(t *testing.T) {
	for _, clearGroup := range []string{"ordinary", "dynamic"} {
		t.Run(clearGroup, func(t *testing.T) {
			clearItem := `{"main_url":"https://media.invalid/clear-1080.mp4","gear_des_key":"0:MP4|1:normal|2:h265_hvc1|4:1080p","check_info":{"check_info":"fixture-checksum"},"video_meta":{"definition":"1080p","vwidth":1920,"vheight":1080,"bitrate":3000000}}`
			protectedItem := `{"main_url":"https://media.invalid/protected-720.mp4","encrypt_info":{"encrypt":true,"spade_a":"fixture-key"},"video_meta":{"definition":"720p","vwidth":1280,"vheight":720,"bitrate":2000000}}`
			ordinaryItem, dynamicItem := clearItem, protectedItem
			if clearGroup == "dynamic" {
				ordinaryItem, dynamicItem = protectedItem, clearItem
			}
			raw := []byte(fmt.Sprintf(`{"code":0,"data":{"video_model":{"video_list":[%s],"dynamic_video":{"dynamic_video_list":[%s],"dynamic_audio_list":[{"main_url":"https://media.invalid/audio.mp4"}]}}}}`, ordinaryItem, dynamicItem))
			playback, err := ParsePlayback(raw, "7100000000000401")
			if err != nil {
				t.Fatal(err)
			}
			best, err := playback.Select("best")
			if err != nil || best.URL != "https://media.invalid/clear-1080.mp4" || best.Definition != "1080p" {
				t.Fatal("最高画质明文组未被保留")
			}
			if playback.Protected || best.Protected || playback.KeyMaterial != "" {
				t.Fatal("明文组被普通校验信息误判，或继承了另一组的加密状态")
			}
			wantAudio := ""
			if clearGroup == "dynamic" {
				wantAudio = "https://media.invalid/audio.mp4"
			}
			if playback.AudioURL != wantAudio {
				t.Fatal("组选优破坏了音轨配对")
			}
		})
	}
}

func TestParsePlaybackClearMetadataKeepsHighestResolutionAndBitrate(t *testing.T) {
	// 与实际普通多档模型相同，每档都有描述和校验数据；修复误判后仍选最高分辨率、最高码率。
	// 故意打乱顺序并让 720p 码率高于 1080p，防止以返回顺序或单独码率替代画质排序。
	raw := []byte(`{"code":0,"data":{"video_model":{"video_list":[
		{"main_url":"https://media.invalid/1080-low.mp4","gear_des_key":"0:MP4|1:normal|2:h265_hvc1|4:1080p","check_info":{"check_info":"fixture-checksum"},"video_meta":{"definition":"1080p","vwidth":1920,"vheight":1080,"bitrate":1000000}},
		{"main_url":"https://media.invalid/360.mp4","gear_des_key":"0:MP4|1:normal|2:h265_hvc1|4:360p","check_info":{"check_info":"fixture-checksum"},"video_meta":{"definition":"360p","vwidth":640,"vheight":360,"bitrate":300000}},
		{"main_url":"https://media.invalid/1080-high.mp4","gear_des_key":"0:MP4|1:normal|2:h265_hvc1|4:1080p","check_info":{"check_info":"fixture-checksum"},"video_meta":{"definition":"1080p","vwidth":1920,"vheight":1080,"bitrate":3000000}},
		{"main_url":"https://media.invalid/720.mp4","gear_des_key":"0:MP4|1:normal|2:h265_hvc1|4:720p","check_info":{"check_info":"fixture-checksum"},"video_meta":{"definition":"720p","vwidth":1280,"vheight":720,"bitrate":7000000}},
		{"main_url":"https://media.invalid/540.mp4","gear_des_key":"0:MP4|1:normal|2:h265_hvc1|4:540p","check_info":{"check_info":"fixture-checksum"},"video_meta":{"definition":"540p","vwidth":960,"vheight":540,"bitrate":600000}}
	]}}}`)
	playback, err := ParsePlayback(raw, "7100000000000401")
	if err != nil {
		t.Fatal(err)
	}
	if len(playback.Streams) != 5 || playback.Protected || playback.KeyMaterial != "" || playback.AudioURL != "" {
		t.Fatal("普通明文档位被丢弃，或错误引入了加密信息与独立音轨")
	}
	for _, stream := range playback.Streams {
		if stream.Protected || stream.Encryption != "" {
			t.Fatal("普通描述和校验数据仍被当作加密标记")
		}
	}
	for _, quality := range []string{"", "best", "1080p"} {
		best, err := playback.Select(quality)
		if err != nil {
			t.Fatal(err)
		}
		if best.URL != "https://media.invalid/1080-high.mp4" || best.Definition != "1080p" || best.Width != 1920 || best.Height != 1080 || best.Bitrate != 3000000 {
			t.Fatalf("修复误判后未保留最高画质和同档最高码率：quality=%q stream=%+v", quality, best)
		}
	}
}
