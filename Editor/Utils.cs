using UnityEngine;

namespace InsaneOne.DevTools
{
	public static class Utils
	{
		/// <summary> We load textures from assets with all compression and post-processing from the asset processor.
		/// So, when interacting with Normal, it is already packaged in a Unity normal format.
		/// We need to unpack (reeturn) Normal-map channels back to default for correct result.</summary>
		public static Texture2D RestoreNormal(Texture2D normal, TextureFormat format = TextureFormat.RGBA32)
		{
			var tex = new Texture2D(normal.width, normal.height, format, true);
			var normalPixels = normal.GetPixels();
			
			for (var q = 0; q < normalPixels.Length; q++)
				normalPixels[q].r = normalPixels[q].a;
			
			tex.SetPixels(normalPixels);
			tex.Apply();
			
			return tex;
		}

		public static Texture2D CreateFilledTexture(int width, int height, Color color, TextureFormat format = TextureFormat.RGBA32)
		{
			var tex = new Texture2D(width, height, format, false);
			var pixels = tex.GetPixels();
			
			for (var q = 0; q < pixels.Length; q++)
				pixels[q] = color;
			
			tex.SetPixels(pixels);
			tex.Apply();

			return tex;
		}
	}
}