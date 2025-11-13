using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using NaughtyAttributes;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json;
using System.Net.Http;
using System.Threading.Tasks;
using Meta.XR;
using PassthroughCameraSamples;

namespace PresentFutures.XRAI.Florence
{
    /// <summary>
    /// Defines all possible tasks for easy selection in the Inspector. (ONLY OBJECT DETECTION IMPLEMENTED FOR NOW)
    /// </summary>
    public enum Florence2Task
    {
        Caption,
        DetailedCaption,
        MoreDetailedCaption,
        ObjectDetection,
        DenseRegionCaption,
        RegionProposal,
        CaptionToPhraseGrounding,
        ReferringExpressionSegmentation,
        RegionToSegmentation,
        OpenVocabularyDetection,
        RegionToCategory,
        RegionToDescription,
        OCR,
        OCRWithRegion
    }

    /// <summary>
    /// A helper class to store the final, processed detection results for drawing.
    /// </summary>
    public class DetectionResult
    {
        public Rect BoundingBox;
        public string Label;
    }

    #region JSON Data Models
    // These classes represent the structure of the JSON response from the API.

    [System.Serializable]
    public class Florence2Response
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("choices")]
        public List<Choice> Choices { get; set; }

        [JsonProperty("usage")]
        public Usage Usage { get; set; }

        [JsonProperty("overlay.png")]
        public string OverlayPngBase64 { get; set; }
    }

    [Serializable]
    public class Choice
    {
        [JsonProperty("index")]
        public int Index { get; set; }

        [JsonProperty("message")]
        public Message Message { get; set; }
    }

    [Serializable]
    public class Message
    {
        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }

        [JsonProperty("entities")]
        public Entities Entities { get; set; }
    }

    [System.Serializable]
    public class Entities
    {
        [JsonProperty("bboxes")]
        public List<List<float>> Bboxes { get; set; }

        [JsonProperty("labels")]
        public List<string> Labels { get; set; }
    }

    [System.Serializable]
    public class Usage
    {
        [JsonProperty("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonProperty("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonProperty("total_tokens")]
        public int TotalTokens { get; set; }
    }
    #endregion

    // ====================================================================================
    // MAIN CONTROLLER CLASS
    // ====================================================================================

    public class Florence2Controller : MonoBehaviour
    {
        [Header("NVIDIA API Settings")]
        [Tooltip("Your NVIDIA API Key")]
        [SerializeField] private ApiConfig apiConfiguration;
        private const string ApiUrl = "https://ai.api.nvidia.com/v1/vlm/microsoft/florence-2";

        [Header("Input")]
        [Tooltip("The image you want to process")]
        public RawImage sourceTexture;

        [Header("Task Selection")]
        public Florence2Task task;
        [Tooltip("E.g., for CaptionToPhraseGrounding or OpenVocabularyDetection")]
        public string textPrompt;
        [Tooltip("E.g., for RegionTo... tasks. Uses normalized coordinates (0-1).")]
        public Rect regionOfInterest = new Rect(0.25f, 0.25f, 0.5f, 0.5f);

        [Header("UI Elements")]
        public TMPro.TMP_Text resultText;
        public RawImage resultImage;
        [Tooltip("UI container (RectTransform) that overlays the result image and will receive the bounding-box UI elements")]
        public RectTransform boundingBoxContainer;
        [Tooltip("Prefab with RectTransform root and TextMeshProUGUI label child")] public GameObject boundingBoxPrefab;
        public TMPro.TMP_Text statusText;

        [Header("Loading UI")]
        [Tooltip("UI GameObject (e.g. spinner) to show while waiting for results")]
        public GameObject loadingIcon;

        public enum FlorenceAnchorMode { BoundingBox2D, SpatialLabel3D, Both }

        [Header("Anchor Mode")]
        [Tooltip("Choose how to visualize detections: 2D bounding boxes or 3D spatial anchors")] 
        public FlorenceAnchorMode anchorMode = FlorenceAnchorMode.BoundingBox2D;

        [Header("Spatial Placement")]
        [Tooltip("Prefab to instantiate at each detected object position")]
        public GameObject spatialAnchorPrefab;
        [Tooltip("Reference to the EnvironmentRaycastManager in the scene")]
        public EnvironmentRaycastManager environmentRaycastManager;
        
        // To store parsed detection results for runtime UI overlay
        private List<DetectionResult> _detectionResults = new List<DetectionResult>();
        private readonly List<GameObject> _spawnedBoxes = new List<GameObject>();
        private readonly List<GameObject> _spawnedAnchors = new List<GameObject>();
        private Texture2D _overlayTexture;

        // Dictionary to map enum to the required API string prompt
        private readonly Dictionary<Florence2Task, string> _taskPrompts = new Dictionary<Florence2Task, string>
        {
            { Florence2Task.Caption, "<CAPTION>" },
            { Florence2Task.DetailedCaption, "<DETAILED_CAPTION>" },
            { Florence2Task.MoreDetailedCaption, "<MORE_DETAILED_CAPTION>" },
            { Florence2Task.ObjectDetection, "<OD>" },
            { Florence2Task.DenseRegionCaption, "<DENSE_REGION_CAPTION>" },
            { Florence2Task.RegionProposal, "<REGION_PROPOSAL>" },
            { Florence2Task.CaptionToPhraseGrounding, "<CAPTION_TO_PHRASE_GROUNDING>" },
            { Florence2Task.ReferringExpressionSegmentation, "<REFERRING_EXPRESSION_SEGMENTATION>" },
            { Florence2Task.RegionToSegmentation, "<REGION_TO_SEGMENTATION>" },
            { Florence2Task.OpenVocabularyDetection, "<OPEN_VOCABULARY_DETECTION>" },
            { Florence2Task.RegionToCategory, "<REGION_TO_CATEGORY>" },
            { Florence2Task.RegionToDescription, "<REGION_TO_DESCRIPTION>" },
            { Florence2Task.OCR, "<OCR>" },
            { Florence2Task.OCRWithRegion, "<OCR_WITH_REGION>" }
        };
        
        [Button]
        public void SendRequest()
        {
            if (resultText != null) resultText.text = "";
            if (statusText != null) statusText.text = "Processing...";
            if (loadingIcon != null) loadingIcon.SetActive(true);
            
            _detectionResults.Clear();
            _overlayTexture = null;
            
            StartCoroutine(SendApiRequest());
        } 
        
        // It prepares the data and then calls our new async method.
        private IEnumerator SendApiRequest()
        {
            if (string.IsNullOrEmpty(apiConfiguration.apiKey) || sourceTexture == null)
            {
                statusText.text = "Error: API Key or Source Image is missing!";
                if (loadingIcon != null) loadingIcon.SetActive(false);
                yield break;
            }

            byte[] imageBytes = EncodeTextureToJPG(sourceTexture.texture);
            string imageBase64 = Convert.ToBase64String(imageBytes);
            string taskPromptString = _taskPrompts[task];
            string finalPrompt = taskPromptString;

            if (task == Florence2Task.CaptionToPhraseGrounding || task == Florence2Task.ReferringExpressionSegmentation || task == Florence2Task.OpenVocabularyDetection)
            {
                finalPrompt += textPrompt;
            }
            
            string content = $"{finalPrompt}<img src=\"data:image/jpeg;base64,{imageBase64}\" />";

            var payload = new
            {
                messages = new[] { new { role = "user", content } },
                max_tokens = 1024
            };
            string jsonPayload = JsonConvert.SerializeObject(payload);

            statusText.text = "Sending request...";
            
            // Start the async network request and get a "Task" handle for it.
            Task<byte[]> sendTask = SendRequestWithHttpClientAsync(jsonPayload);

            // Wait in the coroutine until the async Task is completed.
            // This loop allows the Unity main thread to continue running.
            while (!sendTask.IsCompleted)
            {
                yield return null;
            }

            // Now that the task is complete, we can check for errors and get the result.
            if (sendTask.IsFaulted)
            {
                // If an exception occurred in the async task.
                Debug.LogError($"An error occurred during the web request: {sendTask.Exception}");
                statusText.text = "Error: Request failed.";
                if (loadingIcon != null) loadingIcon.SetActive(false);
            }
            else
            {
                // If the task completed successfully.
                byte[] zipData = sendTask.Result;
                if (zipData != null && zipData.Length > 0)
                {
                    statusText.text = "Success! Processing response...";
                    ProcessZipResponse(zipData);
                }
                else
                {
                    // This case handles HTTP errors like 400, 422, etc., where the task completes
                    // but the result is null as we defined in SendRequestWithHttpClientAsync.
                    statusText.text = "Error: Received an error response from the server.";
                    Debug.LogError("Request completed but returned null or empty data. Check console for specific HTTP error.");
                }
                if (loadingIcon != null) loadingIcon.SetActive(false);
            }
        }

    // Standard .NET HttpClient for full control over the request.
    private async Task<byte[]> SendRequestWithHttpClientAsync(string jsonPayload)
    {
        // Use 'using' to ensure the client is disposed of correctly
        using (var client = new HttpClient())
        {
            // Create the request message
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            
            // Add headers with full control. No extra headers will be added.
            requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiConfiguration.apiKey);
            requestMessage.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/zip"));

            // Add the JSON payload to the request body
            requestMessage.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            // Send the request asynchronously and wait for the response
            var response = await client.SendAsync(requestMessage);

            if (response.IsSuccessStatusCode)
            {
                // If successful, read the response content as a byte array
                return await response.Content.ReadAsByteArrayAsync();
            }
            else
            {
                // If not successful, read the error message and log it
                string errorContent = await response.Content.ReadAsStringAsync();
                Debug.LogError($"Error: {response.StatusCode}\nResponse: {errorContent}");
                // Return null to indicate failure
                return null;
            }
        }
    }
        
    private void ProcessZipResponse(byte[] zipData)
    {
        try
        {
            // Log to confirm the method is called
            Debug.Log($"<color=orange>ProcessZipResponse called with {zipData.Length} bytes of data.</color>");

            using (var memoryStream = new MemoryStream(zipData))
            using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read))
            {
                // Log to check the number of files
                Debug.Log($"<color=yellow>Archive contains {archive.Entries.Count} file(s).</color>");

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    // Log the exact name of every file in the ZIP
                    Debug.Log($"<color=cyan>Found file in zip with full name: '{entry.FullName}'</color>");
                    
                    if (entry.FullName.EndsWith(".response"))
                    {
                        Debug.Log($"<color=green>Found a .response file! Processing as JSON...</color>");

                        using (var reader = new StreamReader(entry.Open()))
                        {
                            string jsonContent = reader.ReadToEnd();
                            Debug.Log($"<color=cyan>--- RECEIVED JSON ---</color>\n{jsonContent}");

                            Florence2Response response = JsonConvert.DeserializeObject<Florence2Response>(jsonContent);

                            if (response == null)
                            {
                                Debug.LogError("JSON Deserialization failed. The response object is null.");
                                if (statusText != null) statusText.text = "Error: Failed to parse JSON.";
                                return;
                            }

                            if (resultText != null)
                            {
                                resultText.text = $"ID: {response.Id}\n";
                                if (response.Choices != null && response.Choices.Count > 0 && response.Choices[0]?.Message?.Entities?.Labels != null)
                                    resultText.text += $"Found {response.Choices[0].Message.Entities.Labels.Count} objects.\n";
                                else
                                    resultText.text += "No objects found in response.\n";
                                resultText.text += $"Usage: {response.Usage.TotalTokens} tokens.";
                            }

                            if (response.Choices != null && response.Choices.Count > 0 && response.Choices[0]?.Message?.Entities != null)
                                DisplayObjectDetectionResults(response.Choices[0].Message.Entities);
                            else
                                Debug.LogWarning("The 'entities' object is missing from the JSON response. No bounding boxes to display.");
                        }
                    }
                    else if (entry.FullName.EndsWith(".png"))
                    {
                        // Optional: You could also handle the overlay.png here if you wanted.
                        Debug.Log("Found overlay.png, skipping for now.");
                    }
                }
            }
            if (statusText != null) statusText.text = "Done!";
        }
        catch (Exception e)
        {
            if (statusText != null) statusText.text = "Error: Failed to read response.";
            Debug.LogError($"Failed to process ZIP response: {e.Message}\n{e.StackTrace}");
        }
    }

        private void DisplayObjectDetectionResults(Entities entities)
        {
            _detectionResults.Clear();
            
            Debug.Log($"<color=green>Found {entities.Bboxes.Count} detections. Processing for display...</color>");

            for (int i = 0; i < entities.Bboxes.Count; i++)
            {
                var bbox = entities.Bboxes[i];
                float x = bbox[0];
                float y = bbox[1];
                // Florence-2 bounding box format is [x1, y1, x2, y2] (top-left & bottom-right)
                float width = bbox[2] - x;
                float height = bbox[3] - y;
                
                // Add detection result for UI usage
                _detectionResults.Add(new DetectionResult
                {
                    BoundingBox = new Rect(x, y, width, height),
                    Label = entities.Labels[i]
                });
                
                Debug.Log($"  - Detected '{entities.Labels[i]}' at box: [x:{x}, y:{y}, w:{width}, h:{height}]");
            }

            // After collecting all detections, create visuals
            StartCoroutine( SpawnDetectionVisuals());
        }
        
        private void DecodeAndDisplayOverlay(string base64Data)
        {
            if (string.IsNullOrEmpty(base64Data)) return;

            var base64String = base64Data.Substring(base64Data.IndexOf(',') + 1);
            byte[] imageBytes = Convert.FromBase64String(base64String);

            _overlayTexture = new Texture2D(2, 2);
            _overlayTexture.LoadImage(imageBytes);
            
            resultImage.texture = _overlayTexture;
        }
        
        public static Texture2D ConvertToTexture2D(Texture texture)
        {
            if (texture == null)
            {
                Debug.LogError("ConvertToTexture2D: texture is null!");
                return null;
            }

            RenderTexture tempRT = RenderTexture.GetTemporary(texture.width, texture.height, 0);
            Graphics.Blit(texture, tempRT);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = tempRT;

            Texture2D tex2D = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            tex2D.ReadPixels(new Rect(0, 0, tempRT.width, tempRT.height), 0, 0);
            tex2D.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(tempRT);

            return tex2D;
        }

        public static byte[] EncodeTextureToJPG(Texture texture, int quality = 75)
        {
            if (texture == null)
            {
                Debug.LogError("EncodeTextureToJPG: Provided texture is null!");
                return null;
            }

            if (texture.width == 0 || texture.height == 0)
            {
                Debug.LogError("EncodeTextureToJPG: Texture has invalid dimensions (0x0). Is webcam started?");
                return null;
            }

            Texture2D tex2D = ConvertToTexture2D(texture);
            return tex2D.EncodeToJPG(quality);
        }

        #region UI Bounding Box Helpers
        private void ClearBoundingBoxes()
        {
            foreach (var go in _spawnedBoxes)
            {
                if (go) Destroy(go);
            }
            _spawnedBoxes.Clear();
            ClearAnchors();
        }

        private IEnumerator SpawnDetectionVisuals()
        {
            if (boundingBoxContainer == null)
            {
                Debug.LogWarning("No boundingBoxContainer assigned – falling back to OnGUI drawing.");
                yield break;
            }

            ClearBoundingBoxes();

            RectTransform imgRect = resultImage.rectTransform;
            float scaleX = imgRect.rect.width / resultImage.texture.width;
            float scaleY = imgRect.rect.height / resultImage.texture.height;

            foreach (var det in _detectionResults)
            {
                if (boundingBoxPrefab == null)
                {
                    Debug.LogError("boundingBoxPrefab not assigned");
                    yield break;
                }
                float x = det.BoundingBox.x * scaleX;
                float y = det.BoundingBox.y * scaleY;
                float w = det.BoundingBox.width * scaleX;
                float h = det.BoundingBox.height * scaleY;
                
                if (anchorMode == FlorenceAnchorMode.BoundingBox2D || anchorMode == FlorenceAnchorMode.Both)
                {
                    GameObject boxGO = Instantiate(boundingBoxPrefab, boundingBoxContainer);
                    boxGO.name = "BBox_" + det.Label;
                    var rt = boxGO.GetComponent<RectTransform>();
                    // Anchor to top-left so we can use positive x and negative y.
                    rt.anchorMin = new Vector2(0, 1);
                    rt.anchorMax = new Vector2(0, 1);
                    rt.pivot = new Vector2(0, 1);
                    rt.anchoredPosition = new Vector2(x, -y); // y inverted
                    rt.sizeDelta = new Vector2(w, h);

                    // find label child
                    var txt = boxGO.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                    if (txt) txt.text = det.Label;
                    
                    _spawnedBoxes.Add(boxGO);
                }
                if ((anchorMode == FlorenceAnchorMode.SpatialLabel3D || anchorMode == FlorenceAnchorMode.Both) && spatialAnchorPrefab != null && environmentRaycastManager != null)
                {
                    int centerX = Mathf.RoundToInt(x + w * 0.5f);
                    int centerY = Mathf.RoundToInt(y + h * 0.5f);
                    int invertedCenterY = resultImage.texture.height - centerY;
                    var cameraScreenPoint = new Vector2Int(centerX, invertedCenterY);
                    
                    var ray = PassthroughCameraUtils.ScreenPointToRayInWorld(PassthroughCameraEye.Left, cameraScreenPoint);

                    if (environmentRaycastManager.Raycast(ray, out EnvironmentRaycastHit hitInfo))
                    {
                        GameObject anchorGo = Instantiate(spatialAnchorPrefab);
                        anchorGo.transform.SetPositionAndRotation(
                            hitInfo.point,
                            Quaternion.LookRotation(hitInfo.normal, Vector3.up));
                        _spawnedAnchors.Add(anchorGo);
                        anchorGo.GetComponentInChildren<TMPro.TextMeshProUGUI>().text = det.Label;
                    }
                }

                yield return new WaitForSeconds(0.1f);
            }
        }
        #endregion

        //Clear all created anchors
        private void ClearAnchors()
        {
            foreach (var go in _spawnedAnchors)
            {
                if (go) Destroy(go);
            }
            _spawnedAnchors.Clear();
        }
    }
}
