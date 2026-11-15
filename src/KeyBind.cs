using System;
using BepInEx.Configuration;
using UnityEngine;

namespace BZMultiplayer
{
    /// <summary>
    /// A key setting stored as plain text ("F5", "End", "None") instead of a KeyCode entry. BepInEx prints every
    /// possible key under a KeyCode setting as "Acceptable values", which turned the [Keys] section of the config
    /// into pages of noise; a string entry carries only its own line. Reads like a ConfigEntry: .Value is the KeyCode.
    /// </summary>
    public sealed class KeyBind
    {
        private readonly ConfigEntry<string> entry;
        private readonly KeyCode fallback;
        private string parsedText;
        private KeyCode parsed;

        public KeyBind(ConfigFile config, string section, string name, KeyCode def, string description)
        {
            fallback = def;
            entry = config.Bind(section, name, def.ToString(), description + " A Unity key name, e.g. F5, End, Home, Keypad1, LeftShift, or None.");
        }

        public KeyCode Value
        {
            get
            {
                string text = entry.Value;
                if (text != parsedText)
                {
                    parsedText = text;
                    KeyCode k;
                    string t = (text ?? "").Trim();
                    if (t.Length == 0) parsed = KeyCode.None;
                    else if (TryParse(t, out k)) parsed = k;
                    else { parsed = fallback; Plugin.Log.LogWarning("Config key '" + entry.Definition.Key + "' = '" + text + "' is not a key name; using " + fallback + "."); }
                }
                return parsed;
            }
            set { entry.Value = value.ToString(); }
        }

        private static bool TryParse(string t, out KeyCode k)
        {
            try { k = (KeyCode)Enum.Parse(typeof(KeyCode), t, true); return true; }
            catch { k = KeyCode.None; return false; }
        }

        public override string ToString() { return Value.ToString(); }
    }
}
