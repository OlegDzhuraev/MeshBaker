/*
 * Copyright 2024 Oleg Dzhuraev <godlikeaurora@gmail.com>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace InsaneOne.DevTools
{
	public sealed class MeshBakerWindow : EditorWindow
	{
		const int MaxUv = 2;
		const int MaxAtlasSize = 2048;
		const TextureFormat TexFormat = TextureFormat.RGBA32;
		
		static readonly int mainTexId = Shader.PropertyToID("_MainTex");
		static readonly int specGlossMapId = Shader.PropertyToID("_SpecGlossMap");
		static readonly int bumpMapId = Shader.PropertyToID("_BumpMap");

		readonly List<MeshFilter> toCombine = new List<MeshFilter>();
		readonly List<MeshFilter> uniqueMeshFilters = new List<MeshFilter>();
		readonly Dictionary<Mesh, Vector2[]> uniqueMeshesUvs = new Dictionary<Mesh, Vector2[]>();
		readonly List<Material> uniqueMaterials = new List<Material>();

		MeshFilter finalMeshFilter;
		bool disableOriginalMeshes;
	
		Texture2D tempNormalTex;

		string bakePostfix = "00";
		
		[MenuItem("Tools/Mesh Baker")]
		public static void ShowWindow()
		{
			var wnd = GetWindow<MeshBakerWindow>();
			wnd.titleContent = new GUIContent("Mesh Baker");
		}

		public void OnGUI()
		{
			EditorGUILayout.HelpBox("Final mesh filter is filled automatically. But allows you to select MeshFilter component, where should be baken combined mesh.", MessageType.Info);
			
			finalMeshFilter = EditorGUILayout.ObjectField("Final mesh filter", finalMeshFilter, typeof(MeshFilter), true) as MeshFilter;
			disableOriginalMeshes = EditorGUILayout.Toggle("Disable original meshes", disableOriginalMeshes);

			bakePostfix = SanitizeFilename(EditorGUILayout.TextField("Baked files postfix", bakePostfix));
			
			var prevEnabled = GUI.enabled;

			if (Selection.gameObjects.Length < 2)
			{
				EditorGUILayout.HelpBox("Select at least 2 meshes to begin bake process.", MessageType.Info);
				GUI.enabled = false;
			}

			if (GUILayout.Button("Bake meshes"))
				ProceedMeshes();

			GUI.enabled = prevEnabled;
		}

		string SanitizeFilename(string input)
		{
			var invalids = Path.GetInvalidFileNameChars();
			return string.Join("_", input.Split(invalids, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
		}

		void ProceedMeshes()
		{
			Cleanup();
			Prepare();
			ProceedUVs();
			
			var newMesh = CombineMeshes();

			if (!finalMeshFilter)
			{
				var go = new GameObject("CombinedMeshes");
				finalMeshFilter = go.AddComponent<MeshFilter>();
				go.AddComponent<MeshRenderer>();
			}
			
			finalMeshFilter.sharedMesh = newMesh;

			ProcessTextures();
		}

		void Cleanup()
		{	
			uniqueMeshesUvs.Clear();
			toCombine.Clear();
			uniqueMaterials.Clear();
			uniqueMeshFilters.Clear();
		}

		void Prepare()
		{
			foreach (var go in Selection.gameObjects)
			{
				if (!go.TryGetComponent<MeshFilter>(out var meshFilter))
					continue;

				toCombine.Add(meshFilter);

				if (disableOriginalMeshes)
					go.SetActive(false);

				if (!uniqueMeshesUvs.ContainsKey(meshFilter.sharedMesh))
				{
					uniqueMeshFilters.Add(meshFilter);
					uniqueMeshesUvs.Add(meshFilter.sharedMesh, Array.Empty<Vector2>());
				}
			}
		}

		void ProceedUVs()
		{
			var cell = 0;
			var row = 0;
			
			foreach (var meshFilter in uniqueMeshFilters)
			{
				if (cell == MaxUv)
				{
					cell = 0;
					row++;
				}

				if (row == MaxUv)
					throw new IndexOutOfRangeException("This baker does not supports this amount of unique UVs! Process terminated.");

				var newUvs = new Vector2[meshFilter.sharedMesh.uv.Length];
				var packToLine = uniqueMeshFilters.Count < 3;
				
				for (var q = 0; q < newUvs.Length; q++)
				{
					var uv = meshFilter.sharedMesh.uv[q];
					
					uv = new Vector2(uv.x / 2, uv.y / (packToLine ? 1f : 2f));
					uv.x += 0.5f * cell;
					uv.y += packToLine ? 1f : 0.5f * row;
						
					newUvs[q] = uv;
				}
				
				uniqueMeshesUvs[meshFilter.sharedMesh] = newUvs;

				if (meshFilter.TryGetComponent<MeshRenderer>(out var meshRenderer))
					uniqueMaterials.Add(meshRenderer.sharedMaterial);
				else
					Debug.LogError("One of baking meshes have no MeshRenderer, so no info about its textures.");
				
				cell++;
			}
		}
		
		Mesh CombineMeshes()
		{
			var combineMeshes = new CombineInstance[toCombine.Count];
			for (var q = 0; q < combineMeshes.Length; q++)
			{
				var meshToCombine = combineMeshes[q];
				meshToCombine.mesh = Instantiate(toCombine[q].sharedMesh);
				meshToCombine.transform = toCombine[q].transform.localToWorldMatrix;
				meshToCombine.mesh.uv = uniqueMeshesUvs[toCombine[q].sharedMesh];
					
				combineMeshes[q] = meshToCombine;
			}

			var mesh = new Mesh();
			mesh.CombineMeshes(combineMeshes);
			Unwrapping.GenerateSecondaryUVSet(mesh);

			return mesh;
		}

		void ProcessTextures()
		{
			var albedos = new Texture2D[uniqueMaterials.Count];
			var speculars = new Texture2D[uniqueMaterials.Count];
			var normals = new Texture2D[uniqueMaterials.Count];

			for (var q = 0; q < uniqueMaterials.Count; q++)
			{
				var uniqueMaterial = uniqueMaterials[q];
				albedos[q] = uniqueMaterial.GetTexture(mainTexId) as Texture2D;
				speculars[q] = uniqueMaterial.GetTexture(specGlossMapId) as Texture2D;

				var width = albedos[q].width;
				var height = albedos[q].height;
				
				if (!speculars[q])
					speculars[q] = CreateFilledTexture(width, height, new Color(0.5f, 0.5f, 0.5f, 1f)); // we need unique tex (when content still same) for unity texture packer - otherwise packed amount will be changed

				var loadedNormal = uniqueMaterial.GetTexture(bumpMapId) as Texture2D;

				if (loadedNormal)
					normals[q] = RestoreNormal(loadedNormal);
				else
					normals[q] = CreateFilledTexture(width, height, new Color(0.5f, 0.5f, 1f, 1f)); 
			}

			var finalAlbedo = MakeAtlas(albedos, "BakedAlbedo");
			var finalSpecular = MakeAtlas(speculars, "BakedSpeculars");
			var finalNormal = MakeAtlas(normals, "BakedNormals");

			var finalMat = new Material(uniqueMaterials[0].shader);
			finalMat.CopyPropertiesFromMaterial(uniqueMaterials[0]);
			
			finalMat.SetTexture(mainTexId, finalAlbedo);
			finalMat.SetTexture(specGlossMapId, finalSpecular);
			finalMat.SetTexture(bumpMapId, finalNormal);

			AssetDatabase.CreateAsset(finalMat, $"{GetPath()}BakedMaterial{bakePostfix}.mat");
			AssetDatabase.SaveAssets();
			AssetDatabase.Refresh();
			
			var meshRend = finalMeshFilter.GetComponent<MeshRenderer>();
			meshRend.sharedMaterial = finalMat;

			finalMat.EnableKeyword("_NORMALMAP");
			finalMat.EnableKeyword("_SPECGLOSSMAP");
		}
		
		string GetPath(bool includeAssets = true)
		{
			var path = "Assets/Baked/";

			if (!AssetDatabase.IsValidFolder(path))
				AssetDatabase.CreateFolder("Assets", "Baked");

			if (!includeAssets)
				path = path.Replace("Assets", "");
			
			return path;
		}
		
		Texture2D MakeAtlas(Texture2D[] textures, string filename)
		{
			var texture = new Texture2D(MaxAtlasSize, MaxAtlasSize, TexFormat, true);
			texture.PackTextures(textures, 0, MaxAtlasSize);

			var bytes = texture.EncodeToPNG();
			var fileName = $"{filename}{bakePostfix}.png";
			
			File.WriteAllBytes(Application.dataPath + GetPath(false) +  fileName, bytes);
			AssetDatabase.Refresh();

			if (filename.Contains("Normal"))
			{
				var importer = TextureImporter.GetAtPath(GetPath() + fileName) as TextureImporter;
				importer.textureType = TextureImporterType.NormalMap;
				importer.SaveAndReimport();
			}

			var finalTex = AssetDatabase.LoadAssetAtPath<Texture2D>(GetPath() + fileName);
			
			return finalTex;
		}

		/// <summary> We load textures from assets with all compression and post-processing from the asset processor.
		/// So, when interacting with Normal, it is already packaged in a Unity normal format.
		/// We need to unpack (reeturn) Normal-map channels back to default for correct result.</summary>
		Texture2D RestoreNormal(Texture2D normal)
		{
			var tex = new Texture2D(normal.width, normal.height, TexFormat, true);
			var normalPixels = normal.GetPixels();
			
			for (var q = 0; q < normalPixels.Length; q++)
				normalPixels[q].r = normalPixels[q].a;
			
			tex.SetPixels(normalPixels);
			tex.Apply();
			
			return tex;
		}

		Texture2D CreateFilledTexture(int width, int height, Color color)
		{
			var tex = new Texture2D(width, height, TexFormat, false);
			var pixels = tex.GetPixels();
			
			for (var q = 0; q < pixels.Length; q++)
				pixels[q] = color;
			
			tex.SetPixels(pixels);
			tex.Apply();

			return tex;
		}
	}
}