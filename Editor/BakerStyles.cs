using UnityEditor;
using UnityEngine;

namespace InsaneOne.DevTools
{
	internal static class BakerStyles
	{
		public static GUIStyle RichLabelStyle { get; }

		static BakerStyles()
		{
			RichLabelStyle = new GUIStyle(EditorStyles.label) { richText = true };
		}
	}
}