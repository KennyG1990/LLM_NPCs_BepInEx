using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Helper class to execute code on the Unity main thread from async tasks.
    /// Required because Unity API can only be called from the main thread.
    /// </summary>
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static UnityMainThreadDispatcher _instance;
        private static readonly object _lock = new object();
        private readonly Queue<Action> _actions = new Queue<Action>();
        private readonly object _actionsLock = new object();

        public static UnityMainThreadDispatcher Instance()
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        var go = new GameObject("UnityMainThreadDispatcher");
                        _instance = go.AddComponent<UnityMainThreadDispatcher>();
                        DontDestroyOnLoad(go);
                    }
                }
            }
            return _instance;
        }

        /// <summary>
        /// Enqueues an action to be executed on the main thread.
        /// </summary>
        public void Enqueue(Action action)
        {
            lock (_actionsLock)
            {
                _actions.Enqueue(action);
            }
        }

        private void Update()
        {
            // Copy actions to execute outside the lock to prevent deadlock
            // if an action calls Enqueue()
            Action[] actionsToExecute;
            lock (_actionsLock)
            {
                actionsToExecute = _actions.ToArray();
                _actions.Clear();
            }
            
            // Execute actions outside the lock
            foreach (var action in actionsToExecute)
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UnityMainThreadDispatcher] Error executing action: {ex}");
                }
            }
        }

        private void OnDestroy()
        {
            lock (_lock)
            {
                if (_instance == this)
                {
                    _instance = null;
                }
            }
        }
    }
}