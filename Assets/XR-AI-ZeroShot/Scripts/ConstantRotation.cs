using System;
using UnityEngine;

namespace PresentFutures.XRAI.Florence
{
    public class ConstantRotation : MonoBehaviour
    {
        [SerializeField] private Vector3 rotationVector;

        private void Update()
        {
            transform.Rotate(rotationVector *  Time.deltaTime);
        }
    }
}
