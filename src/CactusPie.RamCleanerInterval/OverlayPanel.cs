using System.Collections.Generic;
using UnityEngine;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// The top-left panel. The plugin rebuilds the rows once per second (<see cref="Begin"/> ... <see cref="Bar"/>);
    /// OnGUI only draws them. Sections get a tinted title strip and a gap so memory / frames / mods / hitches
    /// don't run into each other.
    /// </summary>
    internal sealed class OverlayPanel
    {
        public static readonly Color Red = new Color(1f, 0.42f, 0.38f);
        public static readonly Color Yellow = new Color(1f, 0.82f, 0.35f);
        public static readonly Color Green = new Color(0.35f, 0.85f, 0.45f);
        public static readonly Color Blue = new Color(0.4f, 0.68f, 1f);
        public static readonly Color Purple = new Color(0.78f, 0.55f, 1f);
        public static readonly Color Gray = new Color(0.62f, 0.62f, 0.62f);
        public static readonly Color Dim = new Color(0.78f, 0.78f, 0.78f);

        private const float Pad = 6f;
        private const float LineHeight = 18f;
        private const float HeaderHeight = 20f;
        private const float SectionGap = 6f;
        private const float NameWidth = 165f;
        private const float BarWidth = 200f;
        private const float ValueWidth = 125f;
        private const float Width = Pad * 2 + NameWidth + BarWidth + ValueWidth + 8f;
        private const float TextWidth = Width - Pad * 2 - 6f;

        private enum Kind
        {
            Header,
            Text,
            Bar,
        }

        private struct Row
        {
            public Kind Kind;
            public string Text;
            public string Right;
            public float Fraction;
            public Color Color;
            public Color Accent;
            public float Height;
        }

        private readonly List<Row> _rows = new List<Row>();
        private Color _sectionAccent = Color.white;
        private GUIStyle _box;
        private GUIStyle _label;
        private GUIStyle _labelRight;
        private GUIStyle _header;
        private GUIStyle _wrap;

        public bool IsEmpty => _rows.Count == 0;

        public void Begin()
        {
            _rows.Clear();
        }

        /// <summary>Starts a section: a tinted title strip, optional summary text on the right.</summary>
        public void Header(string title, Color accent, string right = null)
        {
            _sectionAccent = accent;
            _rows.Add(new Row { Kind = Kind.Header, Text = title, Right = right, Accent = accent });
        }

        public void Text(string text, Color? color = null)
        {
            _rows.Add(new Row { Kind = Kind.Text, Text = text, Color = color ?? Color.white, Accent = _sectionAccent, Height = -1f });
        }

        public void Bar(string name, float fraction, string value, Color color)
        {
            _rows.Add(new Row { Kind = Kind.Bar, Text = name, Right = value, Fraction = Mathf.Clamp01(fraction), Color = color, Accent = _sectionAccent });
        }

        public void Draw(float x, float y)
        {
            if (_rows.Count == 0)
            {
                return;
            }

            EnsureStyles();
            float height = Pad;
            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                if (row.Kind == Kind.Text && row.Height < 0f)
                {
                    // Long lines (suspect messages) wrap instead of being cut off; measured once per rebuild.
                    row.Height = Mathf.Max(LineHeight, _wrap.CalcHeight(new GUIContent(row.Text), TextWidth) + 2f);
                    _rows[i] = row;
                }

                height += row.Kind == Kind.Header ? HeaderHeight + (i > 0 ? SectionGap : 0f) : row.Kind == Kind.Text ? row.Height : LineHeight;
            }

            height += Pad;
            GUI.Box(new Rect(x, y, Width, height), GUIContent.none, _box);
            Color previous = GUI.color;
            float cy = y + Pad;

            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                switch (row.Kind)
                {
                    case Kind.Header:
                        if (i > 0)
                        {
                            cy += SectionGap;
                        }

                        GUI.color = new Color(row.Accent.r, row.Accent.g, row.Accent.b, 0.28f);
                        GUI.DrawTexture(new Rect(x + 2f, cy, Width - 4f, HeaderHeight), Texture2D.whiteTexture);
                        GUI.color = row.Accent;
                        GUI.DrawTexture(new Rect(x + 2f, cy, 3f, HeaderHeight), Texture2D.whiteTexture);
                        GUI.color = Color.white;
                        GUI.Label(new Rect(x + Pad + 4f, cy, Width - Pad * 2, HeaderHeight), row.Text, _header);
                        if (!string.IsNullOrEmpty(row.Right))
                        {
                            GUI.Label(new Rect(x + Pad, cy, Width - Pad * 2 - 2f, HeaderHeight), row.Right, _labelRight);
                        }

                        cy += HeaderHeight;
                        break;

                    case Kind.Text:
                        GUI.color = Color.white;
                        _wrap.normal.textColor = row.Color;
                        GUI.Label(new Rect(x + Pad + 6f, cy, TextWidth, row.Height), row.Text, _wrap);
                        cy += row.Height;
                        break;

                    case Kind.Bar:
                        GUI.color = Color.white;
                        GUI.Label(new Rect(x + Pad + 6f, cy, NameWidth - 6f, LineHeight), row.Text, _label);
                        GUI.color = new Color(1f, 1f, 1f, 0.12f);
                        GUI.DrawTexture(new Rect(x + Pad + NameWidth, cy + 4f, BarWidth, LineHeight - 8f), Texture2D.whiteTexture);
                        GUI.color = row.Color;
                        GUI.DrawTexture(new Rect(x + Pad + NameWidth, cy + 4f, BarWidth * row.Fraction, LineHeight - 8f), Texture2D.whiteTexture);
                        GUI.color = Color.white;
                        GUI.Label(new Rect(x + Pad + NameWidth + BarWidth + 6f, cy, ValueWidth, LineHeight), row.Right, _label);
                        cy += LineHeight;
                        break;
                }
            }

            GUI.color = previous;
        }

        private void EnsureStyles()
        {
            if (_box != null)
            {
                return;
            }

            _box = new GUIStyle(GUI.skin.box);
            _label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            _label.normal.textColor = Color.white;
            _labelRight = new GUIStyle(_label) { alignment = TextAnchor.MiddleRight, fontSize = 12 };
            _labelRight.normal.textColor = Dim;
            _header = new GUIStyle(_label) { fontStyle = FontStyle.Bold };
            _wrap = new GUIStyle(_label) { wordWrap = true, alignment = TextAnchor.UpperLeft };
        }
    }
}
