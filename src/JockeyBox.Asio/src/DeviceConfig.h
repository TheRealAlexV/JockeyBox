#pragma once

// Reader for the app-written virtual-device.json.
//
// The app (src/JockeyBox/Audio/Virtual/VirtualDeviceConfig.cs) writes one canonical
// JSON form; this file parses exactly that subset with no JSON library and without
// touching the heap. The two sides are pinned to the same committed fixture, so a
// writer or reader change that disagrees with the other fails a test instead of
// presenting as silence from the driver.
//
// The file is %ProgramData%\JockeyBox\virtual-device.json, world-readable on purpose:
// an elevated host reads the same file the user-level app wrote. The device is an
// ordered list of lines; its channel count is the sum of the line widths, clamped to
// [2, 32]. A missing file is not an error: it means the legacy stereo pair.
//
// JOCKEYBOX_DEVICE_CONFIG overrides the path. That exists for the harness and tests
// only, so a test never depends on whatever the machine's real app has configured.

#include <windows.h>

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iterator>

namespace jb::asio::config {

constexpr int kMinChannels = 2;
constexpr int kMaxChannels = 32;
constexpr int kMaxLines = 32;
constexpr int kMaxConfigBytes = 32768;

inline constexpr wchar_t kEnvOverride[] = L"JOCKEYBOX_DEVICE_CONFIG";

struct Line {
    char id[64] = {};
    char name[64] = {};
    char source[128] = {};
    int channels = 2;   // 2 for a Stereo Line, 1 for a Mono Line
    double gain = 1.0;
    bool mute = false;
};

struct Device {
    Line lines[kMaxLines];
    int lineCount = 0;
    int channels = kMinChannels;
    bool loaded = false;   // false with an empty error means "no file: stereo default"
    char error[160] = {};
};

namespace detail {

inline void setError(char* target, size_t cap, const char* message, size_t at) noexcept {
    _snprintf_s(target, cap, _TRUNCATE, "%s (at byte %zu)", message, at);
}

inline bool isSpace(char c) noexcept {
    return c == ' ' || c == '\t' || c == '\r' || c == '\n';
}

inline int hexValue(char c) noexcept {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

// Strict recursive-descent parser for the canonical subset. Unknown object members
// are skipped rather than rejected: an older driver must keep reading a file that
// gained an optional field. Anything malformed is an error, never a guess.
class Parser {
public:
    Parser(const char* text, size_t length, Device& device) noexcept
        : m_begin(text), m_p(text), m_end(text + length), m_device(device) {}

    bool parse() noexcept {
        if (!skipSpace() || !take('{')) return fail("root is not an object");
        bool sawVersion = false;
        bool sawLines = false;
        if (!skipSpace()) return false;
        if (!take('}')) {
            for (;;) {
                char key[32] = {};
                if (!skipSpace() || !string(key, sizeof(key))) return false;
                if (!skipSpace() || !take(':')) return fail("expected ':'");
                if (std::strcmp(key, "version") == 0) {
                    double version = 0.0;
                    if (!number(version)) return false;
                    if (version != 1.0) return fail("version is not 1");
                    sawVersion = true;
                } else if (std::strcmp(key, "lines") == 0) {
                    if (!lines()) return false;
                    sawLines = true;
                } else if (!skipValue(0)) {
                    return false;
                }
                if (!skipSpace()) return false;
                if (take(',')) continue;
                if (take('}')) break;
                return fail("expected ',' or '}'");
            }
        }
        skipSpace();
        if (m_p != m_end) return fail("trailing bytes after the root object");
        if (!sawVersion) return fail("missing version");
        if (!sawLines) return fail("missing lines");
        return validate();
    }

private:
    bool lines() noexcept {
        if (!skipSpace() || !take('[')) return fail("lines is not an array");
        if (!skipSpace()) return false;
        if (take(']')) return true;
        for (;;) {
            if (m_device.lineCount >= kMaxLines) return fail("too many lines");
            Line& line = m_device.lines[m_device.lineCount];
            if (!lineObject(line)) return false;
            ++m_device.lineCount;
            if (!skipSpace()) return false;
            if (take(',')) continue;
            if (take(']')) return true;
            return fail("expected ',' or ']' in lines");
        }
    }

    bool lineObject(Line& line) noexcept {
        if (!skipSpace() || !take('{')) return fail("line is not an object");
        bool sawId = false, sawType = false, sawGain = false, sawMute = false;
        if (!skipSpace()) return false;
        if (!take('}')) {
            for (;;) {
                char key[32] = {};
                if (!skipSpace() || !string(key, sizeof(key))) return false;
                if (!skipSpace() || !take(':')) return fail("expected ':'");
                if (std::strcmp(key, "id") == 0) {
                    if (!string(line.id, sizeof(line.id))) return false;
                    sawId = true;
                } else if (std::strcmp(key, "name") == 0) {
                    if (!string(line.name, sizeof(line.name))) return false;
                } else if (std::strcmp(key, "type") == 0) {
                    char type[8] = {};
                    if (!string(type, sizeof(type))) return false;
                    if (std::strcmp(type, "stereo") == 0) line.channels = 2;
                    else if (std::strcmp(type, "mono") == 0) line.channels = 1;
                    else return fail("line type is neither 'stereo' nor 'mono'");
                    sawType = true;
                } else if (std::strcmp(key, "source") == 0) {
                    if (!string(line.source, sizeof(line.source))) return false;
                } else if (std::strcmp(key, "gain") == 0) {
                    if (!number(line.gain)) return false;
                    if (!std::isfinite(line.gain)) return fail("gain is not finite");
                    sawGain = true;
                } else if (std::strcmp(key, "mute") == 0) {
                    if (literal("true")) line.mute = true;
                    else if (literal("false")) line.mute = false;
                    else return fail("mute is not a boolean");
                    sawMute = true;
                } else if (!skipValue(0)) {
                    return false;
                }
                if (!skipSpace()) return false;
                if (take(',')) continue;
                if (take('}')) break;
                return fail("expected ',' or '}'");
            }
        }
        if (!sawId) return fail("line has no id");
        if (!sawType) return fail("line has no type");
        if (!sawGain) return fail("line has no gain");
        if (!sawMute) return fail("line has no mute");
        return true;
    }

    bool validate() noexcept {
        int total = 0;
        for (int i = 0; i < m_device.lineCount; ++i) {
            const Line& line = m_device.lines[i];
            if (line.id[0] == '\0') return fail("a line id is empty");
            for (int j = 0; j < i; ++j)
                if (std::strcmp(line.id, m_device.lines[j].id) == 0)
                    return fail("duplicate line id");
            if (line.channels != 1 && line.channels != 2) return fail("bad line width");
            total += line.channels;
        }
        if (total > kMaxChannels) return fail("line widths exceed 32 channels");
        m_device.channels = total < kMinChannels ? kMinChannels : total;
        return true;
    }

    bool skipSpace() noexcept {
        while (m_p < m_end && isSpace(*m_p)) ++m_p;
        return true;
    }

    bool take(char c) noexcept {
        if (m_p < m_end && *m_p == c) {
            ++m_p;
            return true;
        }
        return false;
    }

    bool literal(const char* text) noexcept {
        const size_t length = std::strlen(text);
        if (static_cast<size_t>(m_end - m_p) < length) return false;
        if (std::memcmp(m_p, text, length) != 0) return false;
        m_p += length;
        return true;
    }

    bool fail(const char* message) noexcept {
        setError(m_error, sizeof(m_error), message, static_cast<size_t>(m_p - m_begin));
        return false;
    }

    bool put(char* out, size_t cap, size_t& used, char c) noexcept {
        if (used + 1 >= cap) return fail("string is longer than the field allows");
        out[used++] = c;
        out[used] = '\0';
        return true;
    }

    bool string(char* out, size_t cap) noexcept {
        if (!skipSpace() || !take('"')) return fail("expected a string");
        size_t used = 0;
        out[0] = '\0';
        while (m_p < m_end) {
            const unsigned char c = static_cast<unsigned char>(*m_p++);
            if (c == '"') return true;
            if (c < 0x20) return fail("control character in a string");
            if (c != '\\') {
                if (!put(out, cap, used, static_cast<char>(c))) return false;
                continue;
            }
            if (m_p >= m_end) return fail("unterminated escape");
            const char esc = *m_p++;
            char decoded = 0;
            switch (esc) {
                case '"': decoded = '"'; break;
                case '\\': decoded = '\\'; break;
                case '/': decoded = '/'; break;
                case 'b': decoded = '\b'; break;
                case 'f': decoded = '\f'; break;
                case 'n': decoded = '\n'; break;
                case 'r': decoded = '\r'; break;
                case 't': decoded = '\t'; break;
                case 'u': {
                    int code = 0;
                    for (int i = 0; i < 4; ++i) {
                        if (m_p >= m_end) return fail("short \\u escape");
                        const int value = hexValue(*m_p++);
                        if (value < 0) return fail("bad \\u escape");
                        code = (code << 4) | value;
                    }
                    if (code >= 0xD800 && code <= 0xDFFF) return fail("surrogate escape");
                    if (code < 0x80) {
                        if (!put(out, cap, used, static_cast<char>(code))) return false;
                    } else if (code < 0x800) {
                        if (!put(out, cap, used, static_cast<char>(0xC0 | (code >> 6))) ||
                            !put(out, cap, used, static_cast<char>(0x80 | (code & 0x3F))))
                            return false;
                    } else {
                        if (!put(out, cap, used, static_cast<char>(0xE0 | (code >> 12))) ||
                            !put(out, cap, used, static_cast<char>(0x80 | ((code >> 6) & 0x3F))) ||
                            !put(out, cap, used, static_cast<char>(0x80 | (code & 0x3F))))
                            return false;
                    }
                    continue;
                }
                default: return fail("unknown escape");
            }
            if (!put(out, cap, used, decoded)) return false;
        }
        return fail("unterminated string");
    }

    // A string in a member we do not know must not be bounded by any field size.
    bool skipString() noexcept {
        if (!skipSpace() || !take('"')) return fail("expected a string");
        while (m_p < m_end) {
            const unsigned char c = static_cast<unsigned char>(*m_p++);
            if (c == '"') return true;
            if (c < 0x20) return fail("control character in a string");
            if (c != '\\') continue;
            if (m_p >= m_end) return fail("unterminated escape");
            const char esc = *m_p++;
            if (esc == 'u') {
                for (int i = 0; i < 4; ++i) {
                    if (m_p >= m_end || hexValue(*m_p) < 0) return fail("bad \\u escape");
                    ++m_p;
                }
            } else if (!std::strchr("\"\\/bfnrt", esc)) {
                return fail("unknown escape");
            }
        }
        return fail("unterminated string");
    }

    bool number(double& out) noexcept {
        if (!skipSpace()) return false;
        const char* begin = m_p;
        if (m_p < m_end && *m_p == '-') ++m_p;
        if (m_p >= m_end || *m_p < '0' || *m_p > '9') return fail("expected a number");
        while (m_p < m_end && *m_p >= '0' && *m_p <= '9') ++m_p;
        if (m_p < m_end && *m_p == '.') {
            ++m_p;
            if (m_p >= m_end || *m_p < '0' || *m_p > '9') return fail("bad fraction");
            while (m_p < m_end && *m_p >= '0' && *m_p <= '9') ++m_p;
        }
        if (m_p < m_end && (*m_p == 'e' || *m_p == 'E')) {
            ++m_p;
            if (m_p < m_end && (*m_p == '+' || *m_p == '-')) ++m_p;
            if (m_p >= m_end || *m_p < '0' || *m_p > '9') return fail("bad exponent");
            while (m_p < m_end && *m_p >= '0' && *m_p <= '9') ++m_p;
        }
        char buffer[64];
        const size_t length = static_cast<size_t>(m_p - begin);
        if (length >= sizeof(buffer)) return fail("number is too long");
        std::memcpy(buffer, begin, length);
        buffer[length] = '\0';
        out = std::strtod(buffer, nullptr);
        return true;
    }

    bool skipValue(int depth) noexcept {
        if (depth > 8) return fail("nesting is too deep");
        if (!skipSpace()) return false;
        if (m_p >= m_end) return fail("unexpected end of file");
        switch (*m_p) {
            case '"':
                return skipString();
            case 't':
                return literal("true");
            case 'f':
                return literal("false");
            case 'n':
                return literal("null");
            case '{':
                ++m_p;
                if (!skipSpace()) return false;
                if (take('}')) return true;
                for (;;) {
                    if (!skipString()) return false;
                    if (!skipSpace() || !take(':')) return fail("expected ':'");
                    if (!skipValue(depth + 1)) return false;
                    if (!skipSpace()) return false;
                    if (take(',')) continue;
                    if (take('}')) return true;
                    return fail("expected ',' or '}'");
                }
            case '[':
                ++m_p;
                if (!skipSpace()) return false;
                if (take(']')) return true;
                for (;;) {
                    if (!skipValue(depth + 1)) return false;
                    if (!skipSpace()) return false;
                    if (take(',')) continue;
                    if (take(']')) return true;
                    return fail("expected ',' or ']'");
                }
            default: {
                double ignored = 0.0;
                return number(ignored);
            }
        }
    }

    const char* m_begin;
    const char* m_p;
    const char* m_end;
    Device& m_device;
    char m_error[160] = {};
};

}  // namespace detail

// Parses a buffer. On failure the device is reset to the stereo default and
// device.error says why; the caller must treat it as "no geometry".
inline bool parse(const char* text, size_t length, Device& device) noexcept {
    const bool ok = detail::Parser(text, length, device).parse();
    if (!ok) {
        device.loaded = false;
        device.channels = kMinChannels;
        device.lineCount = 0;
        return false;
    }
    device.loaded = true;
    return true;
}

// --- file access ------------------------------------------------------------

inline void buildDefaultPath(char* out, size_t cap) noexcept {
    out[0] = '\0';
    DWORD n = GetEnvironmentVariableA("JOCKEYBOX_DEVICE_CONFIG", out, static_cast<DWORD>(cap));
    if (n > 0 && n < cap) return;
    n = GetEnvironmentVariableA("ProgramData", out, static_cast<DWORD>(cap));
    if (n == 0 || n >= cap) {
        strcpy_s(out, cap, "C:\\ProgramData");
    }
    strcat_s(out, cap, "\\JockeyBox\\virtual-device.json");
}

inline bool readFile(const wchar_t* path, char* text, size_t cap, size_t& length,
                     Device& device) noexcept {
    length = 0;
    HANDLE file = CreateFileW(path, GENERIC_READ,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_EXISTING,
                              FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;  // absent: stereo default, not an error
    bool ok = true;
    for (;;) {
        if (length >= cap) {
            detail::setError(device.error, sizeof(device.error),
                             "config file is larger than 32 KB", length);
            ok = false;
            break;
        }
        DWORD got = 0;
        if (!ReadFile(file, text + length, static_cast<DWORD>(cap - length), &got, nullptr)) {
            detail::setError(device.error, sizeof(device.error),
                             "could not read the config file", length);
            ok = false;
            break;
        }
        if (got == 0) break;
        length += got;
    }
    CloseHandle(file);
    return ok;
}

inline bool load(const wchar_t* path, Device& device) noexcept {
    device = Device{};
    char text[kMaxConfigBytes + 1] = {};
    size_t length = 0;
    if (!readFile(path, text, kMaxConfigBytes, length, device)) return false;
    if (length == 0) return false;  // empty file: treat as absent
    text[length] = '\0';
    return parse(text, length, device);
}

// Reads the host's configured path (env override, then %ProgramData%). A missing
// file leaves device.loaded == false, device.channels == 2 and an empty error.
inline bool loadForHost(Device& device) noexcept {
    device = Device{};
    char narrow[MAX_PATH] = {};
    buildDefaultPath(narrow, sizeof(narrow));
    wchar_t wide[MAX_PATH] = {};
    if (MultiByteToWideChar(CP_ACP, 0, narrow, -1, wide,
                            static_cast<int>(std::size(wide))) <= 0)
        return false;
    return load(wide, device);
}

}  // namespace jb::asio::config
