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
using UnityEngine.Rendering;

namespace InsaneOne.DevTools
{
	public sealed class MeshBakerWindow : EditorWindow
	{
		enum TextureType { Albedo, Normal, Specular, Metallic, AO }

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
		bool bakeAo;
		bool sRgbFlagOnSpecular;
		bool saveMesh = true;
		
		MeshFilter targetMeshFilter;
		bool disableOriginalMeshes;

		string bakePostfix = "00";
		string bakeFolder = "Baked";

		bool isSpecularWorkflow;

		int neededVertices;

		Rect[] packingResult;
		
		[MenuItem("Tools/Mesh Baker")]
		public static void ShowWindow()
		{
			var wnd = GetWindow<MeshBakerWindow>();
			wnd.titleContent = new GUIContent("Mesh Baker");
			wnd.minSize = new Vector2(400, 400);
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
			
			GUILayout.Label($"Max final atlas size: <b>{GetAtlasSize()}</b>", BakerStyles.RichLabelStyle);
			
			if (GUILayout.Button("-"))
				ChangeAtlasSize(false);
			
			if (GUILayout.Button("+"))
				ChangeAtlasSize(true);
			
			GUILayout.EndHorizontal();
			
			bakeNormals = EditorGUILayout.Toggle("Normals atlas", bakeNormals);
			bakeSpecular = EditorGUILayout.Toggle("Specular/Metallic atlas", bakeSpecular);
			bakeAo = EditorGUILayout.Toggle("AO atlas", bakeAo);
			sRgbFlagOnSpecular = EditorGUILayout.Toggle(new GUIContent("sRGB on Specular/Metallic", "By design it should be DISABLED on these maps. But some assets was made with this flag enabled. So you can set it enabled here, if your assets using it and you want to keep same look for them."), sRgbFlagOnSpecular);
			saveMesh = EditorGUILayout.Toggle(new GUIContent("Save mesh to assets", "Mesh can be stored directly on scene, or, for better management and scene size reduce, it can be stored in assets."), saveMesh);

			bakeFolder = SanitizeFilename(EditorGUILayout.TextField("Bake folder name", bakeFolder));
			bakePostfix = SanitizeFilename(EditorGUILayout.TextField("Baked files postfix", bakePostfix));
		
			var prevEnabled = GUI.enabled;

			GUILayout.Space(10);
			
			if (Selection.gameObjects.Length < 2)
			{
				EditorGUILayout.HelpBox("Select at least 2 meshes to begin bake process.", MessageType.Info);
				GUI.enabled = false;
			}
			else
			{
				EditorGUILayout.HelpBox("Bake process can take some time.", MessageType.Info);
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
			ProcessTextures();
			ProceedUVs();
			
			var newMesh = CombineMeshes();

			if (saveMesh)
				AssetDatabase.CreateAsset(newMesh, $"{GetPath()}BakedMesh{bakePostfix}.asset");

			targetMeshFilter.sharedMesh = newMesh;
		}

		void Cleanup()
		{	
			uniqueMeshesUvs.Clear();
			toCombine.Clear();
			uniqueMaterials.Clear();
			uniqueMeshFilters.Clear();
			neededVertices = 0;
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

				neededVertices += meshFilter.sharedMesh.vertexCount;

				if (disableOriginalMeshes)
					go.SetActive(false);

				if (!uniqueMeshesUvs.ContainsKey(meshFilter.sharedMesh))
				{
					uniqueMeshFilters.Add(meshFilter);
					uniqueMeshesUvs.Add(meshFilter.sharedMesh, Array.Empty<Vector2>());
					
					if (meshFilter.TryGetComponent<MeshRenderer>(out var meshRenderer))
						uniqueMaterials.Add(meshRenderer.sharedMaterial);
				}
			}

			if (!targetMeshFilter)
			{
				var go = new GameObject("CombinedMeshes");
				targetMeshFilter = go.AddComponent<MeshFilter>();
				go.AddComponent<MeshRenderer>();
			}
		}

		void ProceedUVs()
		{
			for (var q = 0; q < uniqueMeshFilters.Count; q++)
			{
				var meshFilter = uniqueMeshFilters[q];
	
				var newUvs = new Vector2[meshFilter.sharedMesh.uv.Length];
				var newRect = packingResult[q];

				for (var w = 0; w < newUvs.Length; w++)
				{
					var uv = meshFilter.sharedMesh.uv[w];

					uv.x = Mathf.Lerp(newRect.x, newRect.x + newRect.width, uv.x);
					uv.y = Mathf.Lerp(newRect.y, newRect.y + newRect.height, uv.y);

					newUvs[w] = uv;
				}

				uniqueMeshesUvs[meshFilter.sharedMesh] = newUvs;
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
			mesh.indexFormat = neededVertices >= 65536 ? IndexFormat.UInt32 : IndexFormat.UInt16;
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

			var specType = isSpecularWorkflow ? TextureType.Specular : TextureType.Metallic;
			
			// in case of some textures bigger than anothers, we need to rescale them to the size of a smaller texture
			// because current workflow does not supports different sizes on UV.
			ResizeTextures(albedos, TextureType.Albedo);
			ResizeTextures(speculars, specType);
			ResizeTextures(normals, TextureType.Normal);
			ResizeTextures(aos, TextureType.AO);
			
			MakeAtlas(albedos, TextureType.Albedo, finalMat);
			MakeAtlas(speculars, specType, finalMat);
			MakeAtlas(normals, TextureType.Normal, finalMat);
			MakeAtlas(aos, TextureType.AO, finalMat);
		}

		void ResizeTextures(Texture2D[] textures, TextureType type)
		{
			if (!IsNeedBake(type))
				return;
			
			var minResolution = GetAtlasSize();
			
			foreach (var texture in textures)
				if (texture.width < minResolution)
					minResolution = texture.width;

			for (var q = 0; q < textures.Length; q++)
			{
				var rawTexture = textures[q];
				
				Utils.MakeTextureReadable(rawTexture);
				
				if (rawTexture.width > minResolution)
				{
					var newTexture = new Texture2D(rawTexture.width, rawTexture.height, TextureFormat.ARGB32, true);
					newTexture.SetPixels(rawTexture.GetPixels());
					ThirdParty.TextureScale.Bilinear(newTexture, minResolution, minResolution);
					textures[q] = newTexture; 
				}
			}
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
			var packed = texture.PackTextures(textures, 0, atlasSize);

			if (type == TextureType.Albedo)
				packingResult = packed;

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
					importer.sRGBTexture = sRgbFlagOnSpecular;
				
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