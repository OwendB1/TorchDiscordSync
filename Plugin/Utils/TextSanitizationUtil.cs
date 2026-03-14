// Plugin/Utils/TextSanitizationUtil.cs
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace mamba.TorchDiscordSync.Plugin.Utils
{
    /// <summary>
    /// Utility for sanitizing text (player names, messages, etc.)
    /// Removes non-ASCII characters, control characters, and unwanted symbols.
    /// Includes Discord emoji stripping for in-game chat compatibility.
    /// </summary>
    public static class TextSanitizationUtil
    {
        // ============================================================
        // Emoji → ASCII replacement table
        // Only the most common Discord/chat emoji are mapped here.
        // All remaining astral-plane emoji are stripped after substitution.
        // ============================================================
        private static readonly Dictionary<string, string> _emojiAsciiMap =
            new Dictionary<string, string>
            {
                // Faces / emoticons
                { "\U0001F600", ":D"    },  // 😀
                { "\U0001F601", ":D"    },  // 😁
                { "\U0001F602", "xD"    },  // 😂
                { "\U0001F603", ":)"    },  // 😃
                { "\U0001F604", ":)"    },  // 😄
                { "\U0001F605", ":)"    },  // 😅
                { "\U0001F606", "xD"    },  // 😆
                { "\U0001F607", ":)"    },  // 😇
                { "\U0001F608", ">:)"   },  // 😈
                { "\U0001F609", ";)"    },  // 😉
                { "\U0001F60A", ":)"    },  // 😊
                { "\U0001F60B", ":P"    },  // 😋
                { "\U0001F60C", ":)"    },  // 😌
                { "\U0001F60D", "<3"    },  // 😍
                { "\U0001F60E", "8)"    },  // 😎
                { "\U0001F610", ":|"    },  // 😐
                { "\U0001F611", ":|"    },  // 😑
                { "\U0001F612", ":/"    },  // 😒
                { "\U0001F613", ":S"    },  // 😓
                { "\U0001F614", ":("    },  // 😔
                { "\U0001F615", ":/"    },  // 😕
                { "\U0001F616", ":S"    },  // 😖
                { "\U0001F617", ":*"    },  // 😗
                { "\U0001F618", ":*"    },  // 😘
                { "\U0001F619", ":*"    },  // 😙
                { "\U0001F61A", ":*"    },  // 😚
                { "\U0001F61B", ":P"    },  // 😛
                { "\U0001F61C", ";P"    },  // 😜
                { "\U0001F61D", ";P"    },  // 😝
                { "\U0001F61E", ":("    },  // 😞
                { "\U0001F61F", ":("    },  // 😟
                { "\U0001F620", ">:("   },  // 😠
                { "\U0001F621", ">:("   },  // 😡
                { "\U0001F622", ":("    },  // 😢
                { "\U0001F623", ":("    },  // 😣
                { "\U0001F624", ">:("   },  // 😤
                { "\U0001F625", ":_("   },  // 😥
                { "\U0001F626", ":O"    },  // 😦
                { "\U0001F627", ":O"    },  // 😧
                { "\U0001F628", "D:"    },  // 😨
                { "\U0001F629", "D:"    },  // 😩
                { "\U0001F62A", ":_("   },  // 😪
                { "\U0001F62B", ":_("   },  // 😫
                { "\U0001F62C", ">:("   },  // 😬
                { "\U0001F62D", ":_("   },  // 😭
                { "\U0001F62E", ":O"    },  // 😮
                { "\U0001F62F", ":O"    },  // 😯
                { "\U0001F630", "D:"    },  // 😰
                { "\U0001F631", "D:"    },  // 😱
                { "\U0001F632", ":O"    },  // 😲
                { "\U0001F633", ":$"    },  // 😳
                { "\U0001F634", "-_-"   },  // 😴
                { "\U0001F635", "X)"    },  // 😵
                { "\U0001F636", ":-"    },  // 😶
                { "\U0001F637", ":M"    },  // 😷
                // Hands / gestures
                { "\U0001F44D", "(ok)"  },  // 👍
                { "\U0001F44E", "(no)"  },  // 👎
                { "\U0001F44F", "(clap)"},  // 👏
                { "\U0001F64F", "(pray)"},  // 🙏
                { "\u270B",     "(hand)"},  // ✋
                { "\u270C",     "(v)"   },  // ✌
                { "\u270D",     "(pen)" },  // ✍
                { "\U0001F44C", "(ok)"  },  // 👌
                { "\U0001F44B", "(hi)"  },  // 👋
                { "\U0001F440", "(eyes)"},  // 👀
                // Symbols
                { "\u2764",     "<3"    },  // ❤
                { "\U0001F525", "[fire]"},  // 🔥
                { "\U0001F4A5", "[!]"   },  // 💥
                { "\U0001F4AF", "[100]" },  // 💯
                { "\U0001F4A9", "[poo]" },  // 💩
                { "\u2705",     "[ok]"  },  // ✅
                { "\u274C",     "[x]"   },  // ❌
                { "\u26A0",     "[!]"   },  // ⚠
                { "\u2757",     "[!]"   },  // ❗
                { "\u2753",     "[?]"   },  // ❓
                { "\u2713",     "[v]"   },  // ✓
                { "\u2714",     "[v]"   },  // ✔
                { "\u2716",     "[x]"   },  // ✖
                { "\u2717",     "[x]"   },  // ✗
                { "\u2718",     "[x]"   },  // ✘
                { "\U0001F680", "[^]"   },  // 🚀
                { "\U0001F4E3", "[!]"   },  // 📣
                { "\U0001F512", "[lock]"},  // 🔒
                { "\U0001F513", "[open]"},  // 🔓
                { "\U0001F6A8", "[!!!]" },  // 🚨
                { "\u2611",     "[x]"   },  // ☑
                { "\u2B50",     "[*]"   },  // ⭐
                { "\U0001F31F", "[*]"   },  // 🌟
            };

        // ============================================================
        // PUBLIC API
        // ============================================================

        /// <summary>
        /// Sanitize player name - keeps only printable ASCII characters.
        /// Allowed: A-Z, a-z, 0-9, space, underscore, hyphen.
        /// </summary>
        public static string SanitizePlayerName(string playerName)
        {
            if (string.IsNullOrEmpty(playerName))
                return "Unknown";

            try
            {
                // Remove all non-printable-ASCII characters (includes emoji, icons, etc.)
                string sanitized = Regex.Replace(playerName, @"[^\x20-\x7E]", "");

                // Remove remaining control characters
                sanitized = Regex.Replace(sanitized, @"[\x00-\x1F\x7F]", "");

                sanitized = sanitized.Trim();

                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    LoggerUtil.LogWarning(string.Format(
                        "[SANITIZE] Player name completely removed: '{0}' -> using 'Player'",
                        playerName));
                    return "Player";
                }

                if (sanitized != playerName)
                    LoggerUtil.LogDebug(string.Format(
                        "[SANITIZE] Name cleaned: '{0}' -> '{1}'", playerName, sanitized));

                return sanitized;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(string.Format(
                    "[SANITIZE] Error sanitizing name '{0}': {1}", playerName, ex.Message));
                return "Player";
            }
        }

        /// <summary>
        /// Sanitize a chat message - removes control characters but preserves Unicode
        /// for multilingual support. Use StripEmojisFromDiscordMessage separately
        /// when the message is destined for in-game chat.
        /// </summary>
        public static string SanitizeChatMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return "";

            try
            {
                // Remove control characters only; keep Unicode letters/symbols
                string sanitized = Regex.Replace(message, @"[\x00-\x1F\x7F]", "");
                return sanitized.Trim();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(string.Format(
                    "[SANITIZE] Error sanitizing message: {0}", ex.Message));
                return message;
            }
        }

        /// <summary>
        /// Strip emoji from a Discord message before sending it to the in-game chat.
        /// Processing order:
        ///   1. Discord custom emoji  <:name:id>  →  :name:
        ///   2. Discord animated emoji  <a:name:id>  →  :name:
        ///   3. Known Unicode emoji  →  ASCII equivalent from map
        ///   4. Remaining astral-plane characters (surrogate pairs)  →  removed
        ///   5. BMP symbol blocks (U+2300–U+27BF)  →  removed
        ///   6. Variation selectors / zero-width chars  →  removed
        ///   7. Excess whitespace  →  normalised
        /// </summary>
        public static string StripEmojisFromDiscordMessage(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            try
            {
                // --- Step 1 & 2: Discord custom / animated emoji ---
                // <:thumbsup:123456789> → :thumbsup:
                // <a:wave:123456789>    → :wave:
                text = Regex.Replace(text, @"<a?:(\w+):\d+>", ":$1:");

                // --- Step 3: Replace known emoji with ASCII equivalents ---
                foreach (KeyValuePair<string, string> pair in _emojiAsciiMap)
                    text = text.Replace(pair.Key, " " + pair.Value + " ");

                // --- Step 4: Remove remaining astral-plane characters ---
                // In UTF-16 (.NET strings), astral codepoints are stored as surrogate
                // pairs (high surrogate U+D800–DBFF + low surrogate U+DC00–DFFF).
                // Most emoji live in the astral plane.
                text = Regex.Replace(text, @"[\uD800-\uDBFF][\uDC00-\uDFFF]", "");

                // --- Step 5: Remove BMP symbol / emoji blocks ---
                // U+2300-U+23FF  Miscellaneous Technical
                // U+2400-U+243F  Control Pictures
                // U+2440-U+245F  OCR characters
                // U+2460-U+24FF  Enclosed Alphanumerics
                // U+2500-U+257F  Box Drawing
                // U+2580-U+259F  Block Elements
                // U+25A0-U+25FF  Geometric Shapes
                // U+2600-U+26FF  Miscellaneous Symbols (sun, moon, stars, etc.)
                // U+2700-U+27BF  Dingbats
                text = Regex.Replace(text, @"[\u2300-\u27BF]", "");

                // --- Step 6: Remove variation selectors and zero-width chars ---
                text = Regex.Replace(text, @"[\uFE00-\uFE0F]", ""); // variation selectors
                text = Regex.Replace(text, @"[\u200B-\u200D\uFEFF]", ""); // zero-width

                // --- Step 7: Normalise whitespace ---
                text = Regex.Replace(text, @"\s{2,}", " ");

                return text.Trim();
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError(string.Format(
                    "[SANITIZE] Error stripping emojis: {0}", ex.Message));
                return text;
            }
        }

        /// <summary>
        /// Remove Discord/Markdown formatting characters that cause display issues.
        /// </summary>
        public static string RemoveFormattingCharacters(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            try
            {
                text = text.Replace("\u200B", ""); // Zero-width space
                text = text.Replace("\u200C", ""); // Zero-width non-joiner
                text = text.Replace("\u200D", ""); // Zero-width joiner
                return text;
            }
            catch
            {
                return text;
            }
        }
    }
}