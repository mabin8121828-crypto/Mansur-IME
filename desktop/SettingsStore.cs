// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Microsoft.Win32;

namespace Mansur.Next.Desktop
{
    internal interface ISettingsValues
    {
        object Get(string name);
        void Set(string name, object value);
        void Delete(string name);
    }
    internal sealed class RegistrySettingsValues : ISettingsValues
    {
        internal const string KeyPath = @"Software\MansurNext\Settings";
        public object Get(string name)
        { using (var key = Registry.CurrentUser.OpenSubKey(KeyPath)) return key == null ? null : key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); }
        public void Set(string name, object value)
        { using (var key = Registry.CurrentUser.CreateSubKey(KeyPath)) key.SetValue(name, value, value is string ? RegistryValueKind.String : RegistryValueKind.DWord); }
        public void Delete(string name)
        { using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, true)) if (key != null) key.DeleteValue(name, false); }
    }
    internal sealed class Preferences
    {
        internal int Theme, CandidateLayout, FontSize = 12, FuzzyMask, SpeedPercent = 100;
        internal int EnglishWritebackMode = 0;
        internal bool Abbreviation = true, AutoRemember = true, ToolbarVisible = true;
        internal bool SystemSpeechFallback, EnglishSuggestions;
        internal string Voice = "af_heart", UserLexiconPath = "";
        internal int LexiconRevision, ToolbarX = Int32.MinValue, ToolbarY = Int32.MinValue;
        internal static int Bounded(object value, int fallback, int min, int max)
        { return value is int && (int)value >= min && (int)value <= max ? (int)value : fallback; }
    }
    internal sealed class SettingsStore
    {
        private readonly ISettingsValues values;
        internal SettingsStore(ISettingsValues source) { values = source; }
        internal Preferences Read()
        {
            var p = new Preferences();
            p.Theme = Preferences.Bounded(values.Get("Theme"), 0, 0, 2);
            p.CandidateLayout = Preferences.Bounded(values.Get("CandidateLayout"), 0, 0, 1);
            p.FontSize = Preferences.Bounded(values.Get("FontSize"), 12, 10, 20);
            p.Abbreviation = Preferences.Bounded(values.Get("Abbreviation"), 1, 0, 1) != 0;
            p.AutoRemember = Preferences.Bounded(values.Get("AutoRemember"), 1, 0, 1) != 0;
            p.ToolbarVisible = Preferences.Bounded(values.Get("ToolbarVisible"), 1, 0, 1) != 0;
            p.FuzzyMask = Preferences.Bounded(values.Get("FuzzyMask"), 0, 0, 255);
            p.SpeedPercent = Preferences.Bounded(values.Get("SpeedPercent"), 100, 75, 125);
            p.EnglishWritebackMode = Preferences.Bounded(values.Get("EnglishWritebackMode"), 0, 0, 1);
            p.SystemSpeechFallback = Preferences.Bounded(values.Get("SystemSpeechFallback"), 0, 0, 1) != 0;
            p.EnglishSuggestions = Preferences.Bounded(values.Get("EnglishSuggestions"), 0, 0, 1) != 0;
            var voice = values.Get("Voice") as string;
            if (Array.IndexOf(LearningRequest.Voices, voice) >= 0) p.Voice = voice;
            p.UserLexiconPath = values.Get("UserLexiconPath") as string ?? "";
            p.LexiconRevision = Preferences.Bounded(values.Get("LexiconRevision"), 0, 0, Int32.MaxValue);
            p.ToolbarX = Preferences.Bounded(values.Get("ToolbarX"), Int32.MinValue, Int32.MinValue, Int32.MaxValue);
            p.ToolbarY = Preferences.Bounded(values.Get("ToolbarY"), Int32.MinValue, Int32.MinValue, Int32.MaxValue);
            return p;
        }
        // Only the fields edited by the settings dialog are written. Live mode and lexicon pointers stay intact.
        internal void SavePreferences(Preferences p)
        {
            if (p.Theme < 0 || p.Theme > 2 || p.CandidateLayout < 0 || p.CandidateLayout > 1 || p.FontSize < 10 || p.FontSize > 20 ||
                p.FuzzyMask < 0 || p.FuzzyMask > 255 || p.SpeedPercent < 75 || p.SpeedPercent > 125 ||
                p.EnglishWritebackMode < 0 || p.EnglishWritebackMode > 1 || Array.IndexOf(LearningRequest.Voices, p.Voice) < 0)
                throw new ArgumentException("Invalid preference.");
            var changes = new Dictionary<string, object> {
                { "Theme", p.Theme }, { "CandidateLayout", p.CandidateLayout }, { "FontSize", p.FontSize },
                { "Abbreviation", p.Abbreviation ? 1 : 0 }, { "AutoRemember", p.AutoRemember ? 1 : 0 }, { "FuzzyMask", p.FuzzyMask }, { "ToolbarVisible", p.ToolbarVisible ? 1 : 0 },
                { "EnglishWritebackMode", p.EnglishWritebackMode }, { "SystemSpeechFallback", p.SystemSpeechFallback ? 1 : 0 }, { "EnglishSuggestions", p.EnglishSuggestions ? 1 : 0 }, { "Voice", p.Voice }, { "SpeedPercent", p.SpeedPercent }
            };
            SaveWithRollback(changes);
        }
        internal void SetToolbarVisible(bool visible) { values.Set("ToolbarVisible", visible ? 1 : 0); }
        internal void SetToolbarPosition(Point location)
        { SaveWithRollback(new Dictionary<string, object> { { "ToolbarX", location.X }, { "ToolbarY", location.Y } }); }
        internal void SetVoice(string voice)
        { if (Array.IndexOf(LearningRequest.Voices, voice) < 0) throw new ArgumentException(); values.Set("Voice", voice); }
        internal void SetSpeed(int percent)
        { if (percent < 75 || percent > 125) throw new ArgumentException(); values.Set("SpeedPercent", percent); }
        internal void PublishLexicon(string path)
        {
            if (!Path.IsPathRooted(path) || !File.Exists(path)) throw new IOException("Compiled dictionary is missing.");
            int old = Read().LexiconRevision;
            SaveWithRollback(new[] { new KeyValuePair<string, object>("UserLexiconPath", Path.GetFullPath(path)), new KeyValuePair<string, object>("LexiconRevision", old == Int32.MaxValue ? 1 : old + 1) });
        }
        private void SaveWithRollback(IEnumerable<KeyValuePair<string, object>> changes)
        {
            var old = new Dictionary<string, object>();
            foreach (var entry in changes) old.Add(entry.Key, values.Get(entry.Key));
            try { foreach (var entry in changes) values.Set(entry.Key, entry.Value); }
            catch
            {
                bool restored = true;
                foreach (var entry in old)
                    try { if (entry.Value == null) values.Delete(entry.Key); else values.Set(entry.Key, entry.Value); } catch { restored = false; }
                if (!restored) throw new IOException("Settings rollback could not be confirmed.");
                throw;
            }
        }
    }
    internal sealed class Palette
    {
        internal Color Background, Foreground, Muted, Border, Accent;
        internal static Palette From(Preferences p)
        {
            bool dark = p.Theme == 2;
            if (p.Theme == 0)
                try { using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) dark = key != null && Object.Equals(key.GetValue("AppsUseLightTheme"), 0); }
                catch (System.Security.SecurityException) { }
            return dark ? new Palette { Background = Color.FromArgb(28, 34, 43), Foreground = Color.FromArgb(243, 246, 251), Muted = Color.FromArgb(181, 194, 211), Border = Color.FromArgb(87, 110, 138), Accent = Color.FromArgb(108, 184, 255) } :
                new Palette { Background = Color.FromArgb(248, 250, 252), Foreground = Color.FromArgb(20, 29, 41), Muted = Color.FromArgb(86, 98, 114), Border = Color.FromArgb(165, 181, 202), Accent = Color.FromArgb(0, 88, 174) };
        }
    }
}
