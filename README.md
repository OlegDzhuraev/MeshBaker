# Mesh Baker tool
Allows you to combine several meshes, and combine their textures to atlases for optimization purposes.

<p align="center">
    <img src="https://dzhuraev.com/GithubData/MeshBakerTool01.png" width="410" height="419" alt="Mesh Baker Tool">
</p>

# Features
- Supports up to 4 unique meshes per combine (unlimited for instances of the same meshes on scene).
- Supports bake of Albedo, Normal, Specular/Metallic and AO maps to texture atlases. 
- Generates mesh UVs for Lightmapper.

# Limitations
All limitations may be removed in the future versions.
- Textures should have same size.
- Textures should have Read/Write flag enabled.
- Supports only BiRP materials (Standard and Standard (Specular setup)).
- Max atlas size should be set to value x2 bigger than one mesh texture size (2048+ for 1024 textures, for example).

# How to
1. Open Tools -> Mesh Baker in the top menu. 
2. Select required meshes on scene.
3. Press Bake in tool interafce.

# License
Apache 2.0 - free to use in commercial projects.
