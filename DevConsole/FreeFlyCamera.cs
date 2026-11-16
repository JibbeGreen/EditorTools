#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.InputSystem;

namespace Greeny.EditorTools
{
    /// <summary>
    /// Flies the transform it sits on, reading the keyboard and mouse directly (no input actions needed).
    /// Building block for a noclip command: add it to the camera after stopping whatever normally drives
    /// the camera (e.g. the CinemachineBrain) and destroy it to hand control back.
    /// <para>
    /// WASD move, E/Space up, Q down, Shift faster, Ctrl slower, mouse look, scroll changes speed.
    /// Uses unscaled time, so it keeps flying while the game is frozen (timescale 0). Pauses while the console is open.
    /// </para>
    /// </summary>
    [AddComponentMenu("")]
    public sealed class FreeFlyCamera : MonoBehaviour
    {
        public float Speed = 8f;
        public float FastMultiplier = 4f;
        public float SlowMultiplier = 0.25f;
        public float LookSensitivity = 0.1f;

        private const float MinSpeed = 0.5f;
        private const float MaxSpeed = 200f;
        private const float ScrollStep = 1.25f;

        private float _yaw;
        private float _pitch;

        private void OnEnable()
        {
            Vector3 euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        }

        // LateUpdate so nothing that moves the camera in Update gets the last word.
        private void LateUpdate()
        {
            if (DevConsole.IsOpen) return;

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            float deltaTime = Time.unscaledDeltaTime;
            Mouse mouse = Mouse.current;
            Keyboard keyboard = Keyboard.current;

            if (mouse != null)
            {
                Vector2 look = mouse.delta.ReadValue() * LookSensitivity;
                _yaw += look.x;
                _pitch = Mathf.Clamp(_pitch - look.y, -89f, 89f);

                float scroll = mouse.scroll.ReadValue().y;
                if (scroll > 0f) Speed = Mathf.Min(Speed * ScrollStep, MaxSpeed);
                else if (scroll < 0f) Speed = Mathf.Max(Speed / ScrollStep, MinSpeed);
            }

            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

            if (keyboard == null) return;

            Vector3 local = Vector3.zero;
            if (keyboard.wKey.isPressed) local += Vector3.forward;
            if (keyboard.sKey.isPressed) local += Vector3.back;
            if (keyboard.dKey.isPressed) local += Vector3.right;
            if (keyboard.aKey.isPressed) local += Vector3.left;

            float vertical = 0f;
            if (keyboard.eKey.isPressed || keyboard.spaceKey.isPressed) vertical += 1f;
            if (keyboard.qKey.isPressed) vertical -= 1f;

            float speed = Speed;
            if (keyboard.shiftKey.isPressed) speed *= FastMultiplier;
            if (keyboard.ctrlKey.isPressed) speed *= SlowMultiplier;

            // Planar movement follows the view; up/down is always world up, like the scene view.
            Vector3 move = transform.rotation * Vector3.ClampMagnitude(local, 1f) + Vector3.up * vertical;
            transform.position += move * (speed * deltaTime);
        }
    }
}
#endif
