using UnityEngine;

// This attribute allows you to create instances of this object from the Assets menu
[CreateAssetMenu(fileName = "ApiConfig", menuName = "API/API Configuration", order = 1)]
public class ApiConfig : ScriptableObject
{
    [Tooltip("Your secret API key.")]
    public string apiKey;

    // You could add other config values here too, like a base URL
    // public string baseUrl;
}