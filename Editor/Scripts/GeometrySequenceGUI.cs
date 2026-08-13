using UnityEditor;
using UnityEngine;
using System.IO;

namespace BuildingVolumes.Player
{

  [CustomEditor(typeof(GeometrySequencePlayer))]
  [CanEditMultipleObjects]
  public class GeometrySequenceGUI : Editor
  {
    SerializedProperty relativePath;
    SerializedProperty pathRelation;
    SerializedProperty targetFPS;
    SerializedProperty playAtStart;
    SerializedProperty loopPlay;
    SerializedProperty playbackEvents;

    float frameDropShowSeconds = 0.1f;
    float frameDropShowCounter = 0;
    bool eventFoldout;

    private void OnEnable()
    {
      relativePath = serializedObject.FindProperty("relativePath");
      pathRelation = serializedObject.FindProperty("pathRelation");
      targetFPS = serializedObject.FindProperty("playbackFPS");
      playAtStart = serializedObject.FindProperty("playAtStart");
      loopPlay = serializedObject.FindProperty("loopPlay");
      playbackEvents = serializedObject.FindProperty("playbackEvents");


      GeometrySequencePlayer player = (GeometrySequencePlayer)target;
      player.SetupGeometryStream();


      serializedObject.ApplyModifiedProperties();
    }

    public override void OnInspectorGUI()
    {
      serializedObject.Update();

      EditorGUILayout.BeginHorizontal();
      GUILayout.FlexibleSpace();
      if (GUILayout.Button("Documentation", GUILayout.MaxWidth(100)))
      {
        Application.OpenURL("https://buildingvolumes.github.io/Unity_Geometry_Sequence_Player/docs/quickstart/quick-start/");
      }
      EditorGUILayout.EndHorizontal();

      GeometrySequencePlayer player = (GeometrySequencePlayer)target;
      Texture2D logo = (Texture2D)Resources.Load("gss_logo");
      GUIStyle style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
      EditorGUILayout.LabelField(new GUIContent(logo), style, GUILayout.MinHeight(50), GUILayout.MinWidth(128), GUILayout.ExpandWidth(true));
      GUILayout.Space(20);



      GUILayout.Label("Set Sequence", EditorStyles.boldLabel);

      EditorGUILayout.PropertyField(pathRelation);

      relativePath.stringValue = GUILayout.TextField(relativePath.stringValue);

      bool openSequence = false; //Workaround for a Unity GUI bug (EndLayoutGroup: BeginLayoutGroup must be called first)
      GUILayout.BeginHorizontal();
      if (GUILayout.Button("Open Sequence"))
        openSequence = true;

      if (GUILayout.Button("Clear", GUILayout.Width(50)))
      {
        relativePath.stringValue = "";
        player.ClearThumbnail();
      }
      GUILayout.EndHorizontal();

      if (openSequence)
      {
        string path = EditorUtility.OpenFilePanel("Select the sequence.json file inside your sequence folder", relativePath.stringValue, "json");

        if (path != null)
        {
          if (path.Length > 0)
          {
            path = Path.GetDirectoryName(path);

            if (Directory.Exists(path))
            {
              if (BufferedGeometryReader.ContainsGeometryFiles(path))
              {
                if (File.Exists(path + "/sequence.json"))
                {
                  if (path.Contains("StreamingAssets"))
                  {
                    relativePath.stringValue = Path.GetRelativePath(Application.streamingAssetsPath, path);
                    pathRelation.enumValueFlag = (int)GeometrySequenceStream.PathType.RelativeToStreamingAssets;
                  }

                  else if (path.Contains(Application.dataPath))
                  {
                    relativePath.stringValue = Path.GetRelativePath(Application.dataPath, path);
                    pathRelation.enumValueFlag = (int)GeometrySequenceStream.PathType.RelativeToDataPath;
                  }

                  else
                  {
                    relativePath.stringValue = path;
                    pathRelation.enumValueFlag = (int)GeometrySequenceStream.PathType.AbsolutePath;
                  }

                  player.ShowThumbnail(path);
                }

                else
                {
                  EditorUtility.DisplayDialog("Metafile not found", "Could not find sequence.json metadata file, which is required since version 1.1.0 of the plugin. Please download the new v1.1.0 converter tool and re-convert your sequences with it to create the metadata!", "Okay");
                }
              }

              else
              {
                EditorUtility.DisplayDialog("Folder not valid", "Could not find any sequence file in the choosen folder!" +
                                            " Pick another folder, or convert your Geometry Sequence into the correct format with the included converter.",
                                            "Got it!");
              }
            }

          }
        }
      }

#if !UNITY_ANDROID
      if (pathRelation.enumValueIndex != (int)GeometrySequenceStream.PathType.RelativeToStreamingAssets && relativePath.stringValue.Length > 1)
      {
        EditorGUILayout.HelpBox("Files are not placed in the StreamingAsset folder. The playback will still work on your PC, but likely not if you build/export the project to other devices. More information here:", MessageType.Warning);

        if (GUILayout.Button("Open Documentation"))
        {
          Application.OpenURL("https://buildingvolumes.github.io/Unity_Geometry_Sequence_Streaming/docs/tutorials/distribution/");
        }
      }

#endif

#if UNITY_ANDROID
            if (pathRelation.enumValueIndex != (int)GeometrySequenceStream.PathType.RelativeToPersistentDataPath && relativePath.stringValue.Length > 1)
            {
                EditorGUILayout.HelpBox("On Android, files should always be put into the devices Persistent Data Path, other folders are not supported! More information here:", MessageType.Error);

                if (GUILayout.Button("Open Documentation"))
                {
                    Application.OpenURL("https://buildingvolumes.github.io/Unity_Geometry_Sequence_Streaming/docs/tutorials/distribution/");
                }
            }
#endif

      GUILayout.Space(20);
      GUILayout.Label("Playback Settings", EditorStyles.boldLabel);

      EditorGUILayout.BeginHorizontal();
      EditorGUILayout.LabelField("Target Playback FPS:");
      EditorGUI.BeginChangeCheck();
      targetFPS.floatValue = EditorGUILayout.FloatField(targetFPS.floatValue);
      if(EditorGUI.EndChangeCheck())
        player.SetFrameRate(targetFPS.floatValue);
      EditorGUILayout.EndHorizontal();

      EditorGUILayout.BeginHorizontal();
      EditorGUILayout.LabelField("Play at Start:");
      playAtStart.boolValue = EditorGUILayout.Toggle(playAtStart.boolValue);
      EditorGUILayout.EndHorizontal();

      EditorGUILayout.BeginHorizontal();
      EditorGUILayout.LabelField("Loop Playback:");
      loopPlay.boolValue = EditorGUILayout.Toggle(loopPlay.boolValue);
      EditorGUILayout.EndHorizontal();

      eventFoldout = EditorGUILayout.Foldout(eventFoldout, "Events");
      if (eventFoldout)
        EditorGUILayout.PropertyField(playbackEvents);

      GUILayout.Space(20);
      GUILayout.Label("Playback Controls", EditorStyles.boldLabel);

      if (Application.isPlaying)
      {
        GUILayout.BeginHorizontal();
        GUILayout.Label("FPS: " + Mathf.RoundToInt(player.GetActualFPS()), GUILayout.Width(60));

        if (player.GetFrameDropped())
          frameDropShowCounter = 0;

        if (frameDropShowCounter < frameDropShowSeconds)
        {
          Color originalColor = GUI.contentColor;
          GUI.contentColor = new Color(1, 0.5f, 0.5f);
          GUILayout.Label("Frame Dropped!");
          GUI.contentColor = originalColor;
        }

        frameDropShowCounter += Time.deltaTime;

        GUILayout.EndHorizontal();
      }

      GUI.enabled = Application.isPlaying;


      GUILayout.BeginHorizontal();
      float desiredFrame = EditorGUILayout.Slider(player.GetCurrentFrameIndex(), 0, player.GetTotalFrames());
      EditorGUILayout.LabelField("/ " + player.GetTotalFrames().ToString(), GUILayout.Width(80));
      GUILayout.EndHorizontal();

      if (!Mathf.Approximately(desiredFrame, player.GetCurrentFrameIndex()))
        player.GoToFrame((int)desiredFrame);

      GUILayout.BeginHorizontal();

      //Stops the playback and makes it dissappear
      if (GUILayout.Button(EditorGUIUtility.IconContent("PreMatQuad")))
        player.Stop();

      //Rewinds to first frame
      if (GUILayout.Button(EditorGUIUtility.IconContent("Animation.PrevKey")))
        player.GoToTime(0);

      //Goes a few frames back
      if (GUILayout.Button(EditorGUIUtility.IconContent("Profiler.FirstFrame")))
        player.GoToFrame(player.GetCurrentFrameIndex() - (int)targetFPS.floatValue);


      //Pause
      if (player.IsPlaying())
      {
        if (GUILayout.Button(EditorGUIUtility.IconContent("PauseButton")))
          player.Pause();
      }

      //Play
      else
      {
        if (GUILayout.Button(EditorGUIUtility.IconContent("Animation.Play")))
        {
          if (player.IsInitialized())
            player.Play();
          else
            player.LoadCurrentSequence(true);
        }
      }

      //Goes a few frames forward
      if (GUILayout.Button(EditorGUIUtility.IconContent("Profiler.LastFrame")))
        player.GoToFrame(player.GetCurrentFrameIndex() + (int)targetFPS.floatValue);

      GUILayout.EndHorizontal();
      GUI.enabled = true;

      serializedObject.ApplyModifiedProperties();
    }

    public override bool RequiresConstantRepaint()
    {
      return true;
    }
  }
}