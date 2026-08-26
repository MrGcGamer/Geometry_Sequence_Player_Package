using UnityEngine;
using UnityEngine.Rendering;

namespace BuildingVolumes.Player
{
  /// <summary>
  /// Applies a uniform opacity to a mesh sequence's material by switching URP's material-driven
  /// surface state, shared by the two mesh renderers so there is one implementation of the dance.
  /// </summary>
  /// <remarks>
  /// Pointclouds do not use this: their opacity rides a different channel on every path - a shader
  /// uniform on the point-primitive and legacy shaders, the colour render texture's alpha on the
  /// Shader Graph ones. A mesh is an ordinary URP material, so it gets the ordinary treatment.
  /// </remarks>
  static class MeshOpacity
  {
    static readonly int baseColorID = Shader.PropertyToID("_BaseColor");
    static readonly int colorID = Shader.PropertyToID("_Color");
    static readonly int surfaceID = Shader.PropertyToID("_Surface");
    static readonly int blendID = Shader.PropertyToID("_Blend");
    static readonly int srcBlendID = Shader.PropertyToID("_SrcBlend");
    static readonly int dstBlendID = Shader.PropertyToID("_DstBlend");
    static readonly int zWriteID = Shader.PropertyToID("_ZWrite");
    const string surfaceTransparentKeyword = "_SURFACE_TYPE_TRANSPARENT";

    static bool warned;

    public static void Apply(Material mat, float opacity, Object context)
    {
      if (mat == null)
        return;

      if (!mat.HasFloat(surfaceID))
      {
        //Somebody's own material, or a built-in-pipeline one. Say so rather than let a slider sit
        //there doing nothing.
        if (opacity < 1f && !warned)
        {
          warned = true;
          Debug.LogWarning("Shader '" + mat.shader.name + "' has no _Surface property, so the sequence's opacity setting has no effect on it. Use a URP Lit/Unlit material, or one of the package's own.", context);
        }
        return;
      }

      bool blended = opacity < 1f;

      //Tint the base colour rather than adding a property of our own: this is the alpha URP's own
      //shaders already blend with, so nothing has to be taught about it.
      int tintID = mat.HasColor(baseColorID) ? baseColorID : colorID;
      if (mat.HasColor(tintID))
      {
        Color tint = mat.GetColor(tintID);
        tint.a = opacity;
        mat.SetColor(tintID, tint);
      }

      mat.SetFloat(surfaceID, blended ? 1f : 0f);
      mat.SetFloat(blendID, 0f);
      mat.SetFloat(srcBlendID, (float)(blended ? BlendMode.SrcAlpha : BlendMode.One));
      mat.SetFloat(dstBlendID, (float)(blended ? BlendMode.OneMinusSrcAlpha : BlendMode.Zero));

      //Left on while blending, unlike URP's own transparent default. A mesh sequence is a scanned
      //surface that folds over itself constantly; without the depth write its own back faces blend
      //over its front ones in whatever order they happen to be submitted.
      mat.SetFloat(zWriteID, 1f);

      if (blended)
        mat.EnableKeyword(surfaceTransparentKeyword);
      else
        mat.DisableKeyword(surfaceTransparentKeyword);

      mat.renderQueue = blended ? (int)RenderQueue.Transparent : (int)RenderQueue.Geometry;
    }
  }
}
