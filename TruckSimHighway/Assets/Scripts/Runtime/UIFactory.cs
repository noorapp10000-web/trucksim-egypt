using UnityEngine;
using UnityEngine.UI;

namespace TruckSim
{
    public static class UIFactory
    {
        static Font font;
        public static Font Font => font ? font : (font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

        public static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor; rt.pivot = pivot;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            return rt;
        }

        public static Image Image(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size, Color color, Sprite sprite = null)
        {
            var rt = Rect(name, parent, anchor, pivot, pos, size);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color; img.sprite = sprite;
            if (sprite) img.preserveAspect = true;
            return img;
        }

        public static Text Text(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size, string text, int fontSize, TextAnchor align, Color color, FontStyle style = FontStyle.Normal)
        {
            var rt = Rect(name, parent, anchor, pivot, pos, size);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Font; t.text = text; t.fontSize = fontSize; t.alignment = align; t.color = color; t.fontStyle = style;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            var sh = rt.gameObject.AddComponent<Shadow>(); sh.effectColor = new Color(0, 0, 0, 0.6f); sh.effectDistance = new Vector2(2, -2);
            return t;
        }

        public static Button Button(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size, string label, System.Action onClick, int fontSize = 30)
        {
            var img = Image(name, parent, anchor, pivot, pos, size, new Color(0.05f, 0.07f, 0.1f, 0.75f));
            var outline = img.gameObject.AddComponent<Outline>(); outline.effectColor = new Color(1f, 0.72f, 0.1f, 0.85f); outline.effectDistance = new Vector2(2, -2);
            var b = img.gameObject.AddComponent<Button>();
            var colors = b.colors; colors.highlightedColor = new Color(1f, 0.95f, 0.8f); colors.pressedColor = new Color(1f, 0.75f, 0.2f); b.colors = colors;
            b.onClick.AddListener(() => onClick());
            Text("Label", img.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size, label, fontSize, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            return b;
        }

        public static Sprite LoadSprite(string path)
        {
            var tex = Resources.Load<Texture2D>(path);
            if (!tex) { Debug.LogWarning("Missing UI texture " + path); return null; }
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
