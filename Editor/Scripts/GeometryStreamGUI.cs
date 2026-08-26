using UnityEditor;
using UnityEngine;
using System;
using UnityEditor.IMGUI.Controls;

namespace BuildingVolumes.Player
{

  [CustomEditor(typeof(GeometrySequenceStream))]
  [CanEditMultipleObjects]
  public class GeometryStreamGUI : Editor
  {
    GeometrySequenceStream stream;

    SerializedProperty customMaterial;
    SerializedProperty instantiateMaterial;
    SerializedProperty materialSlots;
    SerializedProperty customMaterialSlots;
    SerializedProperty pointSize;
    SerializedProperty pointEmission;
    SerializedProperty pointOpacity;
    SerializedProperty pointSystem;
    SerializedProperty centerContentOnOrigin;

    SerializedProperty bufferSize;
    SerializedProperty useAllThreads;
    SerializedProperty threadCount;

    SerializedProperty droppedFrame;
    SerializedProperty currentFrame;
    SerializedProperty targetFrameTiming;
    SerializedProperty currentFrameTiming;
    SerializedProperty smoothedFPS;

    SerializedProperty attachFrameDebugger;
    SerializedProperty frameDebugger;

    int renderPathIndex = 0;

    bool showInfo;
    bool showBufferOptions;
    bool showMaterialSlots;

    private void OnEnable()
    {
      customMaterial = serializedObject.FindProperty("customMaterial");
      instantiateMaterial = serializedObject.FindProperty("instantiateMaterial");
      materialSlots = serializedObject.FindProperty("materialSlots");
      customMaterialSlots = serializedObject.FindProperty("customMaterialSlots");
      pointSize = serializedObject.FindProperty("pointSize");
      pointEmission = serializedObject.FindProperty("pointEmission");
      pointOpacity = serializedObject.FindProperty("pointOpacity");
      pointSystem = serializedObject.FindProperty("pointRenderPath");
      centerContentOnOrigin = serializedObject.FindProperty("centerContentOnOrigin");

      bufferSize = serializedObject.FindProperty("bufferSize");
      useAllThreads = serializedObject.FindProperty("useAllThreads");
      threadCount = serializedObject.FindProperty("threadCount");

      droppedFrame = serializedObject.FindProperty("frameDropped");
      currentFrame = serializedObject.FindProperty("lastFrameIndex");
      targetFrameTiming = serializedObject.FindProperty("targetFrameTimeMs");
      currentFrameTiming = serializedObject.FindProperty("lastFrameTime");
      smoothedFPS = serializedObject.FindProperty("smoothedFPS");

      attachFrameDebugger = serializedObject.FindProperty("attachFrameDebugger");
      frameDebugger = serializedObject.FindProperty("frameDebugger");

      stream = (GeometrySequenceStream)target;
      renderPathIndex = pointSystem.enumValueIndex;

      serializedObject.ApplyModifiedProperties();
    }

    public override void OnInspectorGUI()
    {
      serializedObject.Update();

      bool updateMaterial = false;
      EditorGUI.BeginChangeCheck();

      EditorGUILayout.PropertyField(customMaterial);
      if (customMaterial.objectReferenceValue != null)
        EditorGUILayout.PropertyField(instantiateMaterial);
      else
        instantiateMaterial.boolValue = true;

      updateMaterial = EditorGUI.EndChangeCheck();

      GUILayout.Space(10);

      GUILayout.Label("Placement", EditorStyles.boldLabel);

      EditorGUI.BeginChangeCheck();
      EditorGUILayout.PropertyField(centerContentOnOrigin, new GUIContent("Center content on origin", "Shifts the sequence by -boundsCenter so its content sits on this transform's pivot. Untick to keep the placement authored into the sequence."));
      if (EditorGUI.EndChangeCheck())
      {
        serializedObject.ApplyModifiedProperties();
        stream.RefreshContentPlacement();
      }

      GUILayout.Space(10);

      GUILayout.Label("Pointcloud Settings", EditorStyles.boldLabel);

      EditorGUILayout.PropertyField(pointSystem);
      if (pointSystem.enumValueIndex != renderPathIndex)
        EditorGUILayout.HelpBox("You have to reload the scene, or enter/exit the playmode once for the changes to take effect", MessageType.Info);

      EditorGUI.BeginChangeCheck();
      EditorGUILayout.PropertyField(pointSize);
      if (EditorGUI.EndChangeCheck())
        stream.SetPointSize(pointSize.floatValue);

      EditorGUI.BeginChangeCheck();
      EditorGUILayout.PropertyField(pointEmission);
      if (EditorGUI.EndChangeCheck())
        stream.SetPointEmission(pointEmission.floatValue);

      EditorGUI.BeginChangeCheck();
      EditorGUILayout.Slider(pointOpacity, 0f, 1f, new GUIContent("Opacity", "Uniform opacity over the whole sequence. Not per-point alpha - the sequence data carries none."));
      if (EditorGUI.EndChangeCheck())
        stream.SetOpacity(pointOpacity.floatValue);

      //Shown only when it applies, so the default look carries no warning about something that is
      //not happening.
      if (pointOpacity.floatValue < 1f)
        EditorGUILayout.HelpBox("Below 1 the cloud is alpha blended. Points are not sorted, so a point hidden behind another can still tint it; nothing disappears, but expect some colour bleed where the cloud overlaps itself.", MessageType.Info);

      GUILayout.Space(10);

      GUILayout.Label("Mesh Settings", EditorStyles.boldLabel);

      showMaterialSlots = EditorGUILayout.Foldout(showMaterialSlots, "Material Slots");
      if (showMaterialSlots)
      {
        EditorGUILayout.PropertyField(materialSlots, new GUIContent("Apply to texture slots: "));
        EditorGUILayout.PropertyField(customMaterialSlots, new GUIContent("Custom texture slots"));
      }

      GUILayout.Label("Playback Settings", EditorStyles.boldLabel);

      showBufferOptions = EditorGUILayout.Foldout(showBufferOptions, "Buffer Options");
      if (showBufferOptions)
      {
        EditorGUILayout.PropertyField(bufferSize);
        if (bufferSize.intValue < 2)
          bufferSize.intValue = 2;

        EditorGUILayout.PropertyField(useAllThreads);
        if (useAllThreads.boolValue)
          GUI.enabled = false;
        EditorGUILayout.PropertyField(threadCount);
        GUI.enabled = true;
      }

      showInfo = EditorGUILayout.Foldout(showInfo, "Frame Info");
      if (showInfo)
      {
        EditorGUI.BeginDisabledGroup(true);
        EditorGUILayout.LabelField("Currently played frame: " + currentFrame.intValue);
        EditorGUILayout.LabelField("Target frame time in ms:    " + targetFrameTiming.floatValue.ToString("0"));
        EditorGUILayout.LabelField("Current frame time in ms:   " + currentFrameTiming.floatValue.ToString("0"));
        EditorGUILayout.LabelField("Smoothed FPS:   " + smoothedFPS.floatValue.ToString("0"));
        EditorGUILayout.LabelField("Dropped frame:  " + droppedFrame.boolValue);
        EditorGUI.EndDisabledGroup();

        GUILayout.Space(10);
        GUILayout.Label("Frame Debugger", EditorStyles.boldLabel);

        EditorGUILayout.PropertyField(attachFrameDebugger, new GUIContent("Attach debugger (Editor only)"));
        EditorGUI.BeginDisabledGroup(attachFrameDebugger.boolValue);
        EditorGUILayout.PropertyField(frameDebugger, new GUIContent("Manually attach debugger"));

      }

      serializedObject.ApplyModifiedProperties();

      if (updateMaterial)
        stream.SetMaterial(stream.customMaterial);
    }
  }
}
