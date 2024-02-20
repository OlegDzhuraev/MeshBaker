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
		enum TextureType { Albedo, Normal, Specular, Metallic, AO }
		
		const int MaxUv = 2;
		const int MaxUniqueMeshes = 4;
		
		static readonly int mainTexId = Shader.PropertyToID("_MainTex");
		static readonly int specGlossMapId = Shader.PropertyToID("_SpecGlossMap");
		static readonly int metallicGlossMapId = Shader.PropertyToID("_MetallicGlossMap");
		static readonly int bumpMapId = Shader.PropertyToID("_BumpMap");
		static readonly int aoMapId = Shader.PropertyToID("_OcclusionMap");

		static readonly List<int> supportedAtlasSizes = new List<int>
		{
			512, 1024, 2048, 4096, 8192
		};

		readonly List<MeshFilter> toCombine = new List<MeshFilter>();
		readonly List<MeshFilter> uniqueMeshFilters = new List<MeshFilter>();
		readonly Dictionary<Mesh, Vector2[]> uniqueMeshesUvs = new Dictionary<Mesh, Vector2[]>();
		readonly List<Material> uniqueMaterials = new List<Material>();

		int selectedAtlasSizeId = 2;
		
		bool bakeNormals = true;
		bool bakeSpecular = true;
		bool bakeAo = false;
		
		MeshFilter targetMeshFilter;
		bool disableOriginalMeshes;

		string bakePostfix = "00";
		string bakeFolder = "Baked";

		GUIStyle richLabelStyle;

		bool isSpecularWorkflow;
		
		[MenuItem("Tools/Mesh Baker")]
		public static void ShowWindow()
		{
			var wnd = GetWindow<MeshBakerWindow>();
			wnd.titleContent = new GUIContent("Mesh Baker");
			wnd.minSize = new Vector2(400, 400);

			wnd.richLabelStyle = new GUIStyle(EditorStyles.label) { richText = true };
		}

		public void OnGUI()
		{
			EditorGUILayout.HelpBox("This tool allows you to combine several meshes into one. Also, it combines their textures to one atlas for draw calls optimization.", MessageType.Info);
			
			GUILayout.Label("Scene settings", EditorStyles.boldLabel);
			EditorGUILayout.HelpBox("Target mesh filter is filled automatically. But allows you to select MeshFilter component, where should be baken combined mesh.", MessageType.Info);
			targetMeshFilter = EditorGUILayout.ObjectField("Target mesh filter", targetMeshFilter, typeof(MeshFilter), true) as MeshFilter;
			disableOriginalMeshes = EditorGUILayout.Toggle("Disable original meshes", disableOriginalMeshes);
			
			GUILayout.Label("Bake settings", EditorStyles.boldLabel);
			
			GUILayout.BeginHorizontal();
			
			GUILayout.Label($"Max final atlas size: <b>{GetAtlasSize()}</b>", richLabelStyle);
			
			if (GUILayout.Button("-"))
				ChangeAtlasSize(false);
			
			if (GUILayout.Button("+"))
				ChangeAtlasSize(true);
			
			GUILayout.EndHorizontal();
			
			bakeNormals = EditorGUILayout.Toggle("Normals atlas", bakeNormals);
			bakeSpecular = EditorGUILayout.Toggle("Specular/Metallic atlas", bakeSpecular);
			bakeAo = EditorGUILayout.Toggle("AO atlas", bakeAo);

			bakeFolder = SanitizeFilename(EditorGUILayout.TextField("Bake folder name", bakeFolder));
			bakePostfix = SanitizeFilename(EditorGUILayout.TextField("Baked files postfix", bakePostfix));
		
			var prevEnabled = GUI.enabled;

			if (Selection.gameObjects.Length < 2)
			{
				EditorGUILayout.HelpBox("Select at least 2 meshes to begin bake process.", MessageType.Info);
				GUI.enabled = false;
			}

			if (GUILayout.Button("Bake"))
				ProceedMeshes();

			GUI.enabled = prevEnabled;
		}

		void ChangeAtlasSize(bool increase)
		{
			selectedAtlasSizeId += increase ? 1 : -1;

			if (selectedAtlasSizeId < 0)
				selectedAtlasSizeId = supportedAtlasSizes.Count - 1;
			else if (selectedAtlasSizeId >= supportedAtlasSizes.Count)
				selectedAtlasSizeId = 0;
		}

		int GetAtlasSize() => supportedAtlasSizes[selectedAtlasSizeId];

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

			if (!targetMeshFilter)
			{
				var go = new GameObject("CombinedMeshes");
				targetMeshFilter = go.AddComponent<MeshFilter>();
				go.AddComponent<MeshRenderer>();
			}
			
			targetMeshFilter.sharedMesh = newMesh;

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
			var path = GetPath();
			path = path.Remove(path.Length - 1); // this is "/" symbol, to make check work correct we need to remove it
			
			if (!AssetDatabase.IsValidFolder(path))
			{
				AssetDatabase.CreateFolder("Assets", bakeFolder);
				AssetDatabase.Refresh();
			}
			
			foreach (var go in Selection.gameObjects)
			{
				if (!go.TryGetComponent<MeshFilter>(out var meshFilter))
					continue;

				if (!go.TryGetComponent<MeshRenderer>(out var renderer) || renderer.sharedMaterial == null)
					throw new NullReferenceException("One of selected Mesh Filters have no Renderer or its material is null! Bake process terminated.");
				
				toCombine.Add(meshFilter);

				if (disableOriginalMeshes)
					go.SetActive(false);

				if (!uniqueMeshesUvs.ContainsKey(meshFilter.sharedMesh))
				{
					uniqueMeshFilters.Add(meshFilter);
					uniqueMeshesUvs.Add(meshFilter.sharedMesh, Array.Empty<Vector2>());
				}
			}
			
			if (uniqueMeshFilters.Count > MaxUniqueMeshes)
				throw new IndexOutOfRangeException($"Maximum unique meshes is {MaxUniqueMeshes}! Bake process terminated.");
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
			var texturesCount = uniqueMaterials.Count;
			var albedos = new Texture2D[texturesCount];
			var speculars = bakeSpecular ? new Texture2D[texturesCount] : Array.Empty<Texture2D>();
			var normals = bakeNormals ? new Texture2D[texturesCount] : Array.Empty<Texture2D>();
			var aos = bakeAo ? new Texture2D[texturesCount] : Array.Empty<Texture2D>();
			
			isSpecularWorkflow = uniqueMaterials[0].GetTexture(specGlossMapId); // todo Mesh Baker - improve check, optimize it.
			
			for (var q = 0; q < texturesCount; q++)
			{
				var uniqueMaterial = uniqueMaterials[q];
				albedos[q] = uniqueMaterial.GetTexture(mainTexId) as Texture2D;

				if (albedos[q] == null)
					throw new NullReferenceException("No Albedo texture in one of meshes materials! Bake process terminated.");
				
				var width = albedos[q].width;
				var height = albedos[q].height;

				if (bakeSpecular)
				{
					speculars[q] = uniqueMaterial.GetTexture(isSpecularWorkflow ? specGlossMapId : metallicGlossMapId) as Texture2D;

					if (!speculars[q])
						speculars[q] = Utils.CreateFilledTexture(width, height, new Color(0.5f, 0.5f, 0.5f, 1f)); // we need unique tex (when content still same) for unity texture packer - otherwise packed amount will be changed
				}

				if (bakeNormals)
				{
					var loadedNormal = uniqueMaterial.GetTexture(bumpMapId) as Texture2D;

					if (loadedNormal)
						normals[q] = Utils.RestoreNormal(loadedNormal);
					else
						normals[q] = Utils.CreateFilledTexture(width, height, new Color(0.5f, 0.5f, 1f, 1f));
				}
				
				if (bakeAo)
				{
					aos[q] = uniqueMaterial.GetTexture(aoMapId) as Texture2D;

					if (!aos[q])
						aos[q] = Utils.CreateFilledTexture(width, height, new Color(1f, 1f, 1f, 1f));
				}
			}

			var finalMat = MakeMaterial(uniqueMaterials[0], isSpecularWorkflow);
			
			MakeAtlas(albedos, TextureType.Albedo, finalMat);
			MakeAtlas(speculars, isSpecularWorkflow ? TextureType.Specular : TextureType.Metallic, finalMat);
			MakeAtlas(normals, TextureType.Normal, finalMat);
			MakeAtlas(aos, TextureType.AO, finalMat);
		}

		Material MakeMaterial(Material originalMat, bool isSpecular)
		{
			var material = new Material(originalMat.shader);
			material.CopyPropertiesFromMaterial(originalMat);
			
			if (bakeNormals)
				material.EnableKeyword("_NORMALMAP");
			
			if (bakeSpecular)
				material.EnableKeyword(isSpecular ? "_SPECGLOSSMAP" : "_METALLICGLOSSMAP");
			
			AssetDatabase.CreateAsset(material, $"{GetPath()}BakedMaterial{bakePostfix}.mat");
			AssetDatabase.SaveAssets();
			AssetDatabase.Refresh();

			var meshRend = targetMeshFilter.GetComponent<MeshRenderer>();
			meshRend.sharedMaterial = material;
			
			return material;
		}
		
		string GetPath(bool includeAssets = true)
		{
			var path = $"Assets/{bakeFolder}/";

			if (!includeAssets)
				path = path.Replace("Assets", "");
			
			return path;
		}
		
		Texture2D MakeAtlas(Texture2D[] textures, TextureType type, Material applyMaterial, TextureFormat format = TextureFormat.RGBA32)
		{
			if (!IsNeedBake(type))
				return null;

			var atlasSize = GetAtlasSize();
			var texture = new Texture2D(atlasSize, atlasSize, format, true);
			texture.PackTextures(textures, 0, atlasSize);

			var bytes = texture.EncodeToPNG();
			var fileName = $"Baked{type}{bakePostfix}.png";
			
			File.WriteAllBytes(Application.dataPath + GetPath(false) +  fileName, bytes);
			AssetDatabase.Refresh();

			var importer = AssetImporter.GetAtPath(GetPath() + fileName) as TextureImporter;

			if (importer)
			{
				importer.textureType = type == TextureType.Normal
					? TextureImporterType.NormalMap
					: TextureImporterType.Default;

				if (type == TextureType.Metallic || type == TextureType.Specular)
					importer.sRGBTexture = false;
				
				importer.SaveAndReimport();
			}

			var finalTex = AssetDatabase.LoadAssetAtPath<Texture2D>(GetPath() + fileName);

			if (applyMaterial)
				applyMaterial.SetTexture(GetMaterialId(type), finalTex);

			return finalTex;
		}

		bool IsNeedBake(TextureType type)
		{
			return type switch
			{
				TextureType.Albedo => true,
				TextureType.Specular => bakeSpecular,
				TextureType.Metallic => bakeSpecular,
				TextureType.Normal => bakeNormals,
				TextureType.AO => bakeAo,
				_ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
			};
		}
		
		int GetMaterialId(TextureType type)
		{
			return type switch
			{
				TextureType.Albedo => mainTexId,
				TextureType.Specular => specGlossMapId,
				TextureType.Metallic => metallicGlossMapId,
				TextureType.Normal => bumpMapId,
				TextureType.AO => aoMapId,
				_ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
			};
		}
	}
}