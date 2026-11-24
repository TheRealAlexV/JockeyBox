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

// Field caps in UTF-8 bytes, NUL excluded. The app validates the same numbers
// (Encoding.UTF8.GetByteCount before it writes: 63 / 63 / 127), so a name that
// fits in the app always fits here and one that does not is refused by name and
// byte count instead of silently shortening the operator's device.
constexpr size_t kIdBytes = 63;       // char id[64]
constexpr size_t kNameBytes = 63;     // char name[64]
constexpr size_t kSourceBytes = 127;  // char source[128]

// Longest accepted number literal, in bytes. System.Text.Json, which parses the
// same file in the app, refuses a number of 64 characters; fail at that boundary
// with a reason rather than let strtod accept what the writer would reject.
constexpr size_t kMaxNumberChars = 63;

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

    // The refusal text, for the caller that reports it: the parser owns its
    // message, so parse() copies it out when the parse fails.
    const char* error() const noexcept { return m_error; }

    bool parse() noexcept {
        skipBom();
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
                    // A second "lines" would be merged into the same list and
                    // double the channel count; a second "version" hides which one
                    // was meant. The writer never emits either, so refuse both.
                    if (sawVersion) return fail("duplicate 'version'");
                    long version = 0;
                    if (!integer(version)) return false;
                    if (version != 1) return fail("version is not 1");
                    sawVersion = true;
                } else if (std::strcmp(key, "lines") == 0) {
                    if (sawLines) return fail("duplicate 'lines'");
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
        bool sawId = false, sawName = false, sawType = false, sawSource = false;
        bool sawGain = false, sawMute = false;
        if (!skipSpace()) return false;
        if (!take('}')) {
            for (;;) {
                char key[32] = {};
                if (!skipSpace() || !string(key, sizeof(key))) return false;
                if (!skipSpace() || !take(':')) return fail("expected ':'");
                if (std::strcmp(key, "id") == 0) {
                    if (sawId) return fail("duplicate 'id'");
                    if (!field(line.id, sizeof(line.id), "id")) return false;
                    sawId = true;
                } else if (std::strcmp(key, "name") == 0) {
                    if (sawName) return fail("duplicate 'name'");
                    if (!field(line.name, sizeof(line.name), "name")) return false;
                    sawName = true;
                } else if (std::strcmp(key, "type") == 0) {
                    if (sawType) return fail("duplicate 'type'");
                    char type[8] = {};
                    if (!string(type, sizeof(type))) return false;
                    if (std::strcmp(type, "stereo") == 0) line.channels = 2;
                    else if (std::strcmp(type, "mono") == 0) line.channels = 1;
                    else return fail("line type is neither 'stereo' nor 'mono'");
                    sawType = true;
                } else if (std::strcmp(key, "source") == 0) {
                    if (sawSource) return fail("duplicate 'source'");
                    if (!field(line.source, sizeof(line.source), "source")) return false;
                    sawSource = true;
                } else if (std::strcmp(key, "gain") == 0) {
                    if (sawGain) return fail("duplicate 'gain'");
                    if (!number(line.gain)) return false;
                    if (!std::isfinite(line.gain)) return fail("gain is not finite");
                    sawGain = true;
                } else if (std::strcmp(key, "mute") == 0) {
                    if (sawMute) return fail("duplicate 'mute'");
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
        // The writer always emits every member; a missing one is a torn or
        // hand-edited file, not a shorter form to be guessed at.
        if (!sawId) return fail("line has no id");
        if (!sawName) return fail("line has no name");
        if (!sawType) return fail("line has no type");
        if (!sawSource) return fail("line has no source");
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

    // A UTF-8 BOM is legal at the start of a file and MessagePack-free C# writers
    // emit one when asked to; it is not part of the JSON document.
    void skipBom() noexcept {
        if (static_cast<size_t>(m_end - m_p) >= 3 &&
            static_cast<unsigned char>(m_p[0]) == 0xEF &&
            static_cast<unsigned char>(m_p[1]) == 0xBB &&
            static_cast<unsigned char>(m_p[2]) == 0xBF) {
            m_p += 3;
        }
    }

    // Decodes a known field and enforces its UTF-8 byte cap. A value one byte over
    // is refused by name and byte count instead of truncated: the app validates
    // the same numbers, so a name that fits there always fits here.
    bool field(char* out, size_t cap, const char* label) noexcept {
        size_t total = 0;
        if (!decodeString(out, cap, &total)) return false;
        if (total > cap - 1) {
            _snprintf_s(m_error, sizeof(m_error), _TRUNCATE,
                        "%s is %zu bytes; the limit is %zu", label, total, cap - 1);
            return false;
        }
        return true;
    }

    // Appends a decoded byte. With a counter the value is measured in full even
    // past the cap, so the caller can report the real length; without one the cap
    // is a hard field limit and overflow is a parse error.
    bool put(char* out, size_t cap, size_t& used, char c, size_t* counter) noexcept {
        if (counter) {
            ++*counter;
            if (used + 1 < cap) out[used++] = c;
            return true;
        }
        if (used + 1 >= cap) return fail("string is longer than the field allows");
        out[used++] = c;
        return true;
    }

    bool string(char* out, size_t cap) noexcept { return decodeString(out, cap, nullptr); }

    bool decodeString(char* out, size_t cap, size_t* counter) noexcept {
        if (!skipSpace() || !take('"')) return fail("expected a string");
        size_t used = 0;
        out[0] = '\0';
        while (m_p < m_end) {
            const unsigned char c = static_cast<unsigned char>(*m_p++);
            if (c == '"') {
                out[used] = '\0';
                return true;
            }
            if (c < 0x20) return fail("control character in a string");
            if (c != '\\') {
                if (!put(out, cap, used, static_cast<char>(c), counter)) return false;
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
                        if (!put(out, cap, used, static_cast<char>(code), counter)) return false;
                    } else if (code < 0x800) {
                        if (!put(out, cap, used, static_cast<char>(0xC0 | (code >> 6)), counter) ||
                            !put(out, cap, used, static_cast<char>(0x80 | (code & 0x3F)), counter))
                            return false;
                    } else {
                        if (!put(out, cap, used, static_cast<char>(0xE0 | (code >> 12)), counter) ||
                            !put(out, cap, used, static_cast<char>(0x80 | ((code >> 6) & 0x3F)),
                                 counter) ||
                            !put(out, cap, used, static_cast<char>(0x80 | (code & 0x3F)), counter))
                            return false;
                    }
                    continue;
                }
                default: return fail("unknown escape");
            }
            if (!put(out, cap, used, decoded, counter)) return false;
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

    // Strict JSON integer. The only member that needs one is "version"; the writer
    // emits exactly 1, so 1.0 and 1e0 are refusals, not the same value. Saturation
    // above 2 keeps a long literal from overflowing, and the caller compares to 1.
    bool integer(long& out) noexcept {
        if (!skipSpace()) return false;
        const char* begin = m_p;
        const bool negative = m_p < m_end && *m_p == '-';
        if (negative) ++m_p;
        if (m_p >= m_end || *m_p < '0' || *m_p > '9') return fail("expected an integer");
        if (*m_p == '0' && m_p + 1 < m_end && m_p[1] >= '0' && m_p[1] <= '9')
            return fail("number has a leading zero");
        long long value = 0;
        while (m_p < m_end && *m_p >= '0' && *m_p <= '9') {
            if (value <= 2) value = value * 10 + (*m_p - '0');
            ++m_p;
        }
        // 1.0 and 1e0 are JSON numbers but not integers; the writer only emits 1.
        if (m_p < m_end && (*m_p == '.' || *m_p == 'e' || *m_p == 'E'))
            return fail("expected an integer");
        if (static_cast<size_t>(m_p - begin) > kMaxNumberChars)
            return fail("number is longer than 63 characters");
        out = negative ? -static_cast<long>(value) : static_cast<long>(value);
        return true;
    }

    bool number(double& out) noexcept {
        if (!skipSpace()) return false;
        const char* begin = m_p;
        if (m_p < m_end && *m_p == '-') ++m_p;
        if (m_p >= m_end || *m_p < '0' || *m_p > '9') return fail("expected a number");
        // Strict JSON: a leading zero is only legal for the single-digit 0.
        if (*m_p == '0' && m_p + 1 < m_end && m_p[1] >= '0' && m_p[1] <= '9')
            return fail("number has a leading zero");
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
        if (length > kMaxNumberChars) return fail("number is longer than 63 characters");
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
    detail::Parser parser(text, length, device);
    const bool ok = parser.parse();
    if (!ok) {
        // The parser owns its message; without this copy the refusal reaches the
        // debug log as an empty reason and the host is never told anything.
        _snprintf_s(device.error, sizeof(device.error), _TRUNCATE, "%s", parser.error());
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
    if (file == INVALID_HANDLE_VALUE) {
        // Only "there is no file" is the legacy stereo default. Access denied, a
        // sharing violation and every other failure refuse the device instead of
        // presenting as two channels: the app wrote something this reader cannot
        // see, and silence from a lying device is worse than a refusal.
        const DWORD error = GetLastError();
        if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) return false;
        _snprintf_s(device.error, sizeof(device.error), _TRUNCATE,
                    "config file could not be opened (Windows error %lu)",
                    static_cast<unsigned long>(error));
        return false;
    }
    bool ok = true;
    for (;;) {
        if (length >= cap) {
            _snprintf_s(device.error, sizeof(device.error), _TRUNCATE,
                        "config file is larger than 32 KB (at byte %zu)", length);
            ok = false;
            break;
        }
        DWORD got = 0;
        if (!ReadFile(file, text + length, static_cast<DWORD>(cap - length), &got, nullptr)) {
            _snprintf_s(device.error, sizeof(device.error), _TRUNCATE,
                        "config file could not be read (Windows error %lu)",
                        static_cast<unsigned long>(GetLastError()));
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
    // False with an empty error means "no file": the stereo default, not a fault.
    if (!readFile(path, text, kMaxConfigBytes, length, device)) return false;
    if (length == 0) {
        // A non-atomic writer caught between create and write leaves this. It is
        // not "unconfigured"; it is a file whose content is unknown.
        _snprintf_s(device.error, sizeof(device.error), _TRUNCATE,
                    "config file is empty; refusing to guess the line list");
        return false;
    }
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
