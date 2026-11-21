using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.UI;

namespace SharedWorlds.UI;

/// <summary>
/// One visual language for every Shared Worlds screen. The palette, hover behavior, title
/// tab and tag artwork are copied from Terraria's own "fancy UI" screens (world select,
/// character select) so the mod reads as part of the game instead of an overlay on it.
/// </summary>
internal static class SwUI
{
	// Vanilla fancy-UI palette, taken from UIWorldSelect / UIWorldListItem.
	public static readonly Color ContainerBg = new Color(33, 43, 79) * 0.8f;
	public static readonly Color ItemBg = new Color(63, 82, 151) * 0.7f;
	public static readonly Color ItemBorder = new Color(89, 116, 213) * 0.7f;
	public static readonly Color ItemBgHover = new Color(73, 94, 171);
	public static readonly Color ItemBorderHover = new Color(89, 116, 213);
	public static readonly Color ButtonBg = new Color(63, 82, 151) * 0.7f;
	public static readonly Color ButtonBorder = Color.Black;
	public static readonly Color ButtonBgHover = new Color(73, 94, 171);
	public static readonly Color ButtonBorderHover = Colors.FancyUIFatButtonMouseOver;
	public static readonly Color TabBg = new Color(73, 94, 171);

	// Accent colors shared by status text and tags.
	public static readonly Color Live = new(112, 226, 168);
	public static readonly Color Warn = new(255, 203, 116);
	public static readonly Color Muted = new(155, 163, 205);
	public static readonly Color Good = new(126, 220, 186);
	public static readonly Color Bright = new(205, 214, 255);

	private static Asset<Texture2D>? _divider;
	private static Asset<Texture2D>? _tagTexture;

	public static Asset<Texture2D> Divider
		=> _divider ??= Main.Assets.Request<Texture2D>("Images/UI/Divider");

	public static Asset<Texture2D> TagTexture
		=> _tagTexture ??= Main.Assets.Request<Texture2D>("Images/UI/InnerPanelBackground");

	/// <summary>The root layout every screen shares: the vanilla world-select frame.</summary>
	public static UIElement MakeRoot(float maxWidth = 670f, float top = 220f)
	{
		return new UIElement {
			Width = StyleDimension.FromPercent(0.8f),
			MaxWidth = StyleDimension.FromPixels(maxWidth),
			Top = StyleDimension.FromPixels(top),
			Height = StyleDimension.FromPixelsAndPercent(-top, 1f),
			HAlign = 0.5f
		};
	}

	/// <summary>The overlapping title tab vanilla fancy screens use.</summary>
	public static UITextPanel<string> TabTitle(string text)
	{
		var tab = new UITextPanel<string>(text, 0.8f, large: true) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(-40f)
		};
		tab.SetPadding(15f);
		tab.BackgroundColor = TabBg;
		return tab;
	}

	/// <summary>The main container panel vanilla fancy screens put their list in.</summary>
	public static UIPanel MakeContainer(float bottomGap = 110f)
	{
		return new UIPanel {
			Width = StyleDimension.FromPercent(1f),
			Height = StyleDimension.FromPixelsAndPercent(-bottomGap, 1f),
			BackgroundColor = ContainerBg
		};
	}

	/// <summary>Vanilla hover: light the button up and play the menu tick, like the world-select buttons.</summary>
	public static void WireHover(UIPanel button)
	{
		button.OnMouseOver += (evt, _) => {
			SoundEngine.PlaySound(SoundID.MenuTick);
			((UIPanel)evt.Target).BackgroundColor = ButtonBgHover;
			((UIPanel)evt.Target).BorderColor = ButtonBorderHover;
		};
		button.OnMouseOut += (evt, _) => {
			((UIPanel)evt.Target).BackgroundColor = ButtonBg;
			((UIPanel)evt.Target).BorderColor = ButtonBorder;
		};
	}

	/// <summary>A large bottom-corner button like vanilla's Back/New, anchored by the caller.</summary>
	public static UITextPanel<string> FatButton(string text, Action onClick, float hAlign = 0f)
	{
		var button = new UITextPanel<string>(text, 0.7f, large: true) {
			Width = StyleDimension.FromPixelsAndPercent(-10f, 0.5f),
			Height = StyleDimension.FromPixels(50f),
			VAlign = 1f,
			HAlign = hAlign,
			Top = StyleDimension.FromPixels(-45f)
		};
		WireHover(button);
		button.OnLeftClick += (_, _) => onClick();
		return button;
	}

	/// <summary>A standard small button with the vanilla look.</summary>
	public static UITextPanel<string> Button(string text, float width, Action onClick, float scale = 0.78f, bool clickSound = true)
	{
		var button = new UITextPanel<string>(text, scale) {
			Width = StyleDimension.FromPixels(width),
			Height = StyleDimension.FromPixels(34f)
		};
		WireHover(button);
		button.OnLeftClick += (_, _) => {
			if (clickSound)
				SoundEngine.PlaySound(SoundID.MenuTick);
			onClick();
		};
		return button;
	}

	/// <summary>Restores the not-hovered list-item colors, optionally the vanilla "dimmed" variant.</summary>
	public static void SetItemColors(UIPanel panel, bool hovered, bool dimmed = false)
	{
		if (hovered) {
			panel.BackgroundColor = ItemBgHover;
			panel.BorderColor = ItemBorderHover;
		}
		else if (dimmed) {
			panel.BackgroundColor = Color.Lerp(ItemBg, new Color(80, 80, 80), 0.5f);
			panel.BorderColor = new Color(127, 127, 127) * 0.7f;
		}
		else {
			panel.BackgroundColor = ItemBg;
			panel.BorderColor = ItemBorder;
		}
	}

	/// <summary>Crops text with an ellipsis so it fits a pixel width at the given font scale.</summary>
	public static string CropToFit(string text, float maxWidth, float scale = 1f)
	{
		if (string.IsNullOrEmpty(text))
			return text;

		var font = FontAssets.MouseText.Value;
		if (font.MeasureString(text).X * scale <= maxWidth)
			return text;

		string trimmed = text;
		while (trimmed.Length > 1 && font.MeasureString(trimmed + "…").X * scale > maxWidth)
			trimmed = trimmed[..^1];

		return trimmed + "…";
	}

	/// <summary>Draws one of the vanilla inner-panel tags (the world-select difficulty/size chips).
	/// The text is cropped to the chip so a long status can never spill onto its neighbours.</summary>
	public static void DrawTag(SpriteBatch sb, Vector2 position, float width, string text, Color color)
	{
		Texture2D texture = TagTexture.Value;
		int height = texture.Height;
		sb.Draw(texture, position, new Rectangle(0, 0, 8, height), Color.White);
		sb.Draw(texture, new Vector2(position.X + 8f, position.Y), new Rectangle(8, 0, 8, height), Color.White, 0f, Vector2.Zero, new Vector2((width - 16f) / 8f, 1f), SpriteEffects.None, 0f);
		sb.Draw(texture, new Vector2(position.X + width - 8f, position.Y), new Rectangle(16, 0, 8, height), Color.White);

		string fitted = CropToFit(text, Math.Max(20f, width - 16f));
		Vector2 size = FontAssets.MouseText.Value.MeasureString(fitted);
		Utils.DrawBorderString(sb, fitted, position + new Vector2(Math.Max(4f, width * 0.5f - size.X * 0.5f), 3f), color);
	}

	/// <summary>Draws the vanilla divider line used under each world name.</summary>
	public static void DrawDivider(SpriteBatch sb, Vector2 position, float width)
	{
		sb.Draw(Divider.Value, position, null, Color.White, 0f, Vector2.Zero, new Vector2(width / 8f, 1f), SpriteEffects.None, 0f);
	}
}
