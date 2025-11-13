using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using NaughtyAttributes;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using Newtonsoft.Json;
using System.Net.Http;
using System.Threading.Tasks;
using Meta.XR;
using PassthroughCameraSamples;

namespace PresentFutures.XRAI.GroundingDino
{
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
    public class GroundingDinoResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("choices")]
        public List<Choice> Choices { get; set; }
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
    public class GroundingDinoBoundingBoxGroup
    {
        [JsonProperty("phrase")] public string Phrase { get; set; }
        [JsonProperty("bboxes")] public List<List<float>> Bboxes { get; set; }
        [JsonProperty("confidence")] public List<float> Confidence { get; set; }
    }

    [Serializable]
    public class GroundingDinoContent
    {
        [JsonProperty("frameNo")] public int FrameNo { get; set; }
        [JsonProperty("frameWidth")] public int FrameWidth { get; set; }
        [JsonProperty("frameHeight")] public int FrameHeight { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("boundingBoxes")] public List<GroundingDinoBoundingBoxGroup> BoundingBoxes { get; set; }
    }

    [Serializable]
    public class Message
    {
        [JsonProperty("role")]
        public string Role { get; set; }

        // For Grounding DINO responses, content is a structured object, not a string
        [JsonProperty("content")]
        public GroundingDinoContent Content { get; set; }
    }


    // Removed Usage model for simplicity (not used)
    #endregion

    #region JSON Data Models for Asset Upload
    [System.Serializable]
    public class AssetUploadResponse
    {
        [JsonProperty("assetId")]
        public string AssetId { get; set; }

        [JsonProperty("uploadUrl")]
        public string UploadUrl { get; set; }
    }
    #endregion

    public class GroundingDinoController : MonoBehaviour
    {
        [Header("NVIDIA API Settings")]
        [Tooltip("Your NVIDIA API Key")]
        [SerializeField] private ApiConfig apiConfiguration;
        private const string InvokeUrl = "https://ai.api.nvidia.com/v1/cv/nvidia/nv-grounding-dino";
        private const string AssetUrl = "https://api.nvcf.nvidia.com/v2/nvcf/assets";
        private const string PollingUrlBase = "https://api.nvcf.nvidia.com/v2/nvcf/pexec/status/";

        [Header("Input")]
        [Tooltip("The image you want to process")]
        public RawImage sourceTexture;

        [Header("Task Selection")]
        [Tooltip("A natural language prompt describing what to detect. E.g., 'a cat, a dog, and a car'")]
        public string textPrompt;
        
        [Tooltip("Confidence threshold for detections (0.0 to 1.0)")]
        [Range(0f, 1f)]
        public float threshold = 0.3f;

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

        public enum AnchorMode { BoundingBox2D, SpatialLabel3D, Both }

        [Header("Anchor Mode")]
        [Tooltip("Choose how to visualize detections: 2D bounding boxes or 3D spatial anchors")] 
        public AnchorMode anchorMode = AnchorMode.BoundingBox2D;

        [Header("Spatial Placement")]
        [Tooltip("Prefab to instantiate at each detected object position")]
        public GameObject spatialAnchorPrefab;
        [Tooltip("Reference to the EnvironmentRaycastManager in the scene")]
        public EnvironmentRaycastManager environmentRaycastManager;
        
        private List<DetectionResult> _detectionResults = new List<DetectionResult>();
        private readonly List<GameObject> _spawnedBoxes = new List<GameObject>();
        private readonly List<GameObject> _spawnedAnchors = new List<GameObject>();
        private const string AssetDescription = "Image for GroundingDino";
        [Range(1, 120)] public int pollingMaxRetries = 20;
        [Range(0.1f, 10f)] public float pollingIntervalSeconds = 1f;

        [Button]
        public void SendRequest()
        {
            if (resultText != null) resultText.text = "";
            if (statusText != null) statusText.text = "Processing...";
            if (loadingIcon != null) loadingIcon.SetActive(true);
            
            _detectionResults.Clear();
            
            StartCoroutine(SendApiRequest());
        } 
        
        private IEnumerator SendApiRequest()
        {
            if (string.IsNullOrEmpty(apiConfiguration.apiKey) || sourceTexture == null)
            {
                statusText.text = "Error: API Key or Source Image is missing!";
                if (loadingIcon != null) loadingIcon.SetActive(false);
                yield break;
            }

            byte[] imageBytes = EncodeTextureToJPG(sourceTexture.texture);
            
            // This task will run the entire async process: asset upload, invocation, and polling
            Task<byte[]> fullRequestTask = PerformFullRequestLifecycle(imageBytes);

            // Wait in the coroutine until the async Task is completed.
            while (!fullRequestTask.IsCompleted)
            {
                yield return null;
            }

            // Now that the task is complete, we can check for errors and get the result.
            if (fullRequestTask.IsFaulted)
            {
                // If an exception occurred in the async task.
                Debug.LogError($"An error occurred during the web request: {fullRequestTask.Exception}");
                statusText.text = "Error: Request failed.";
                if (loadingIcon != null) loadingIcon.SetActive(false);
            }
            else
            {
                // If the task completed successfully.
                byte[] zipData = fullRequestTask.Result;
                if (zipData != null && zipData.Length > 0)
                {
                    statusText.text = "Success! Processing response...";
                    ProcessZipResponse(zipData);
                }
                else
                {
                    // This case handles HTTP errors where the task completes but the result is null.
                    statusText.text = "Error: Received an error response from the server.";
                    Debug.LogError("Request completed but returned null or empty data. Check console for specific HTTP error.");
                }
                if (loadingIcon != null) loadingIcon.SetActive(false);
            }
        }

    private async Task<byte[]> PerformFullRequestLifecycle(byte[] imageBytes)
    {
        // 1. Get Asset Upload URL
        statusText.text = "Requesting asset upload URL...";
        AssetUploadResponse assetInfo = await GetAssetUploadUrl();
        if (assetInfo == null) return null;

        // 2. Upload Image to Asset URL
        statusText.text = "Uploading image...";
        bool uploadSuccess = await UploadAsset(assetInfo.UploadUrl, imageBytes, AssetDescription);
        if (!uploadSuccess) return null;

        // 3. Invoke the model with the Asset ID
        statusText.text = "Invoking model...";
        var payload = new
        {
            model = "Grounding-Dino",
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = textPrompt },
                        new { type = "media_url", media_url = new { url = $"data:image/jpeg;asset_id,{assetInfo.AssetId}" } }
                    }
                }
            },
            threshold = this.threshold
        };
        string jsonPayload = JsonConvert.SerializeObject(payload);
        
        HttpResponseMessage initialResponse = await InvokeModel(jsonPayload, assetInfo.AssetId);
        if (initialResponse == null) return null;

        // 4. Handle response: Poll if 202, otherwise process directly
        if (initialResponse.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            if (!initialResponse.Headers.TryGetValues("NVCF-REQID", out var values))
            {
                Debug.LogError("Server returned 202 Accepted but did not provide an NVCF-REQID header for polling.");
                return null;
            }
            string requestId = new List<string>(values)[0];
            statusText.text = "Waiting for result...";
            return await PollForResult(requestId);
        }
        else if (initialResponse.IsSuccessStatusCode)
        {
            return await initialResponse.Content.ReadAsByteArrayAsync();
        }
        else
        {
            string errorContent = await initialResponse.Content.ReadAsStringAsync();
            Debug.LogError($"Error during model invocation: {initialResponse.StatusCode}\n{errorContent}");
            return null;
        }
    }

    private async Task<AssetUploadResponse> GetAssetUploadUrl()
    {
        using (var client = new HttpClient())
        {
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, AssetUrl);
            requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiConfiguration.apiKey);
            requestMessage.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            var payload = new { contentType = "image/jpeg", description = AssetDescription };
            requestMessage.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var response = await client.SendAsync(requestMessage);

            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<AssetUploadResponse>(json);
            }
            else
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                Debug.LogError($"Error getting asset upload URL: {response.StatusCode}\n{errorContent}");
                return null;
            }
        }
    }

    private async Task<bool> UploadAsset(string uploadUrl, byte[] data, string description)
    {
        // Use UnityWebRequest with manual configuration for the PUT request to S3.
        // The pre-signed URL contains authentication, and we must match the exact headers
        // that were used to generate the signature.
        using (var uwr = new UnityWebRequest(uploadUrl, "PUT"))
        {
            uwr.uploadHandler = new UploadHandlerRaw(data);
            uwr.downloadHandler = new DownloadHandlerBuffer();
            
            // These headers must match what the server expects for the signature
            uwr.SetRequestHeader("Content-Type", "image/jpeg");
            uwr.SetRequestHeader("x-amz-meta-nvcf-asset-description", description);

            var asyncOp = uwr.SendWebRequest();

            while (!asyncOp.isDone)
            {
                await Task.Yield();
            }

            if (uwr.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("Asset uploaded successfully!");
                return true;
            }
            else
            {
                string errorDetails = uwr.downloadHandler != null ? uwr.downloadHandler.text : "No response body";
                Debug.LogError($"Error uploading asset: {uwr.responseCode}\n{uwr.error}\n{errorDetails}");
                return false;
            }
        }
    }

    private async Task<HttpResponseMessage> InvokeModel(string jsonPayload, string assetId)
    {
        using (var client = new HttpClient())
        {
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, InvokeUrl);
            requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiConfiguration.apiKey);
            requestMessage.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/zip"));
            requestMessage.Headers.Add("NVCF-INPUT-ASSET-REFERENCES", assetId);
            requestMessage.Headers.Add("NVCF-FUNCTION-ASSET-IDS", assetId);

            requestMessage.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            // Use SendAsync to get the full HttpResponseMessage back
            return await client.SendAsync(requestMessage);
        }
    }

    private async Task<byte[]> PollForResult(string requestId)
    {
        string pollingUrl = PollingUrlBase + requestId;
        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiConfiguration.apiKey);
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/zip"));

            int maxRetries = pollingMaxRetries;
            for (int i = 0; i < maxRetries; i++)
            {
                int delayMs = Mathf.RoundToInt(pollingIntervalSeconds * 1000f);
                await Task.Delay(delayMs);
                statusText.text = $"Polling for result... (Attempt {i + 1}/{maxRetries})";
                
                var response = await client.GetAsync(pollingUrl);

                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    // Result is ready
                    return await response.Content.ReadAsByteArrayAsync();
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
                {
                    // Still processing, continue polling
                    continue;
                }
                else
                {
                    // An error occurred during polling
                    string errorContent = await response.Content.ReadAsStringAsync();
                    Debug.LogError($"Error polling for result: {response.StatusCode}\n{errorContent}");
                    return null;
                }
            }
        }
        Debug.LogError("Polling timed out. The result was not ready in time.");
        return null;
    }

    private void ProcessZipResponse(byte[] zipData)
    {
        try
        {
            Debug.Log($"<color=orange>ProcessZipResponse called with {zipData.Length} bytes of data.</color>");

            using (var memoryStream = new MemoryStream(zipData))
            using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read))
            {
                Debug.Log($"<color=yellow>Archive contains {archive.Entries.Count} file(s).</color>");

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    Debug.Log($"<color=cyan>Found file in zip with full name: '{entry.FullName}'</color>");
                    
                    if (entry.FullName.EndsWith(".response"))
                    {
                        Debug.Log($"<color=green>Found a .response file! Processing as JSON...</color>");

                        using (var reader = new StreamReader(entry.Open()))
                        {
                            string jsonContent = reader.ReadToEnd();
                            Debug.Log($"<color=cyan>--- RECEIVED JSON ---</color>\n{jsonContent}");

                            GroundingDinoResponse response = JsonConvert.DeserializeObject<GroundingDinoResponse>(jsonContent);

                            if (response == null)
                            {
                                Debug.LogError("JSON Deserialization failed. The response object is null.");
                                if (statusText != null) statusText.text = "Error: Failed to parse JSON.";
                                return;
                            }

                            if (resultText != null)
                            {
                                // Compose summary from Grounding DINO content only
                                var msg = response.Choices?[0]?.Message;
                                if (msg?.Content != null)
                                {
                                    int totalBoxes = 0;
                                    if (msg.Content.BoundingBoxes != null)
                                    {
                                        foreach (var group in msg.Content.BoundingBoxes)
                                            totalBoxes += group.Bboxes != null ? group.Bboxes.Count : 0;
                                    }
                                    resultText.text = $"Grounding DINO Response\nObjects: {totalBoxes}\nLabels: {msg.Content.Message}";
                                }
                                else
                                {
                                    resultText.text = "No objects found in response.";
                                }
                            }

                            var content = response.Choices?[0]?.Message?.Content;
                            if (content?.BoundingBoxes != null && content.BoundingBoxes.Count > 0)
                            {
                                DisplayGroundingDinoResults(content);
                            }
                            else
                            {
                                Debug.LogWarning("No bounding boxes found in Grounding DINO content.");
                            }
                        }
                    }
                    // Ignoring any additional files (e.g., overlay images) for simplicity
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

        // Removed Florence-style Entities fallback for simplicity
        
        private void DisplayGroundingDinoResults(GroundingDinoContent content)
        {
            _detectionResults.Clear();
            if (content.BoundingBoxes == null)
            {
                Debug.LogWarning("GroundingDinoResults: content.BoundingBoxes is null");
                return;
            }

            int added = 0;
            foreach (var group in content.BoundingBoxes)
            {
                if (group.Bboxes == null) continue;
                for (int i = 0; i < group.Bboxes.Count; i++)
                {
                    var bbox = group.Bboxes[i];
                    if (bbox.Count < 4) continue;
                    float conf = (group.Confidence != null && group.Confidence.Count > i) ? group.Confidence[i] : 1f;
                    if (conf < threshold) continue; // apply threshold filter

                    float x = bbox[0];
                    float y = bbox[1];
                    float width = bbox[2];
                    float height = bbox[3];

                    _detectionResults.Add(new DetectionResult
                    {
                        BoundingBox = new Rect(x, y, width, height),
                        Label = $"{group.Phrase} ({conf:F2})"
                    });
                    added++;
                    Debug.Log($"Detected '{group.Phrase}' conf={conf:F2} box: [x:{x}, y:{y}, w:{width}, h:{height}]");
                }
            }

            Debug.Log($"<color=green>GroundingDINO: Added {added} filtered boxes.</color>");
            StartCoroutine(SpawnDetectionVisuals(_detectionResults));
        }
        
        // Removed overlay decode/display for simplicity
        
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

        public IEnumerator SpawnDetectionVisuals(List<DetectionResult> results)
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

            foreach (var det in results)
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
                
                if (anchorMode == AnchorMode.BoundingBox2D || anchorMode == AnchorMode.Both)
                {
                    GameObject boxGO = Instantiate(boundingBoxPrefab, boundingBoxContainer);
                    boxGO.name = "BBox_" + det.Label;
                    var rt = boxGO.GetComponent<RectTransform>();
                    rt.anchorMin = new Vector2(0, 1);
                    rt.anchorMax = new Vector2(0, 1);
                    rt.pivot = new Vector2(0, 1);
                    rt.anchoredPosition = new Vector2(x, -y); // y inverted
                    rt.sizeDelta = new Vector2(w, h);

                    var txt = boxGO.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                    if (txt) txt.text = det.Label;
                    
                    _spawnedBoxes.Add(boxGO);
                }
                if ((anchorMode == AnchorMode.SpatialLabel3D || anchorMode == AnchorMode.Both) && spatialAnchorPrefab != null && environmentRaycastManager != null)
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
