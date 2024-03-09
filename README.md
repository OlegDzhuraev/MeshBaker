# Mesh Baker tool
Allows you to combine several meshes into one, and combine their textures to atlases for optimization purposes.

<p align="center">
    <img src="https://dzhuraev.com/GithubData/MeshBakerTool01.png" width="410" height="419" alt="Mesh Baker Tool">
</p>

# Features
- Supports almost unlimited meshes per combine (but it is necessary to use common sense, personally determining the necessary limit).
- Supports bake of Albedo, Normal, Specular/Metallic and AO maps to texture atlases. 
- Generates mesh UVs for Lightmapper.
- Supports mesh save to the Assets folder.
- Supports generated meshes with up to 4 billion vertices (but not guaranteed to rendered on all platforms. In this case, try to keep vertex count lower than 65536).

# Limitations
Limitations may be removed in the future versions.
- Supports only BiRP materials (Standard and Standard (Specular setup)).

# How to
1. Open **Tools -> Mesh Baker** in the top menu. 
2. Select required meshes on scene.
3. Change parameters to fit your needs.
4. Press **Bake** in the tool interface.

# Recommendations
- Textures are better to have square aspect ratio (non-square is supported, but not in the most efficient way). Also, if textures have different sizes, them will be scaled to a smaller one in the final texture atlas.
- Selected meshes should have same material to allow tool produce correct results.

# Additional info
- This tool sets **Read/Write** flag value to **enabled** on all processed textures (to be able operate them).

# License
Apache 2.0 - free to use in commercial projects.
