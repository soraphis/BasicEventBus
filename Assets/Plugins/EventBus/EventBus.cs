

using Unity.Collections.LowLevel.Unsafe;

namespace EventBus
{
    using System;
    using System.Reflection;
    using System.Collections.Generic;
    using UnityEngine;
    using EventBus.Internal;
    using UnityEngine.Pool;

#if UNITY_EDITOR
    using UnityEditor;
#endif

    /// <summary>
    /// Marks an assembly containing EventBus types.
    /// Used to identify relevant assemblies for the EventBus system cleanup steps.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public class EventBusAssemblyAttribute : Attribute
    {
        
    }
    
    public static class EventBusUtility
    {
        public static IReadOnlyList<Type> EventTypes { get; private set; }
        public static IReadOnlyList<Type> StaticEventBusesTypes { get; private set; }

#if UNITY_EDITOR
        public static PlayModeStateChange PlayModeState { get; private set; }

        [InitializeOnLoadMethod]
        public static void InitializeEditor()
        {
            EditorApplication.playModeStateChanged -= HandleEditorStateChange;
            EditorApplication.playModeStateChanged += HandleEditorStateChange;
        }

        private static void HandleEditorStateChange(PlayModeStateChange state)
        {
            PlayModeState = state;

            if (PlayModeState == PlayModeStateChange.EnteredEditMode)
                ClearAllBuses();
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Init()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            List<Type[]> assemblyTypesToScan = ListPool<Type[]>.Get();

            for (int i = 0; i < assemblies.Length; i++)
            {
                if (assemblies[i].GetName().Name == "Assembly-CSharp" 
                    || assemblies[i].GetName().Name == "Assembly-CSharp-firstpass" 
                    || assemblies[i].GetCustomAttribute<EventBusAssemblyAttribute>() is not null)
                    assemblyTypesToScan.Add(assemblies[i].GetTypes());
            }

            List<Type> eventTypes = new List<Type>();
            foreach (var assemblyTypes in assemblyTypesToScan)
            {
                for (int i = 0; i < assemblyTypes.Length; i++)
                {
                    var type = assemblyTypes[i];
                    if ((typeof(IEvent)) != type && (typeof(IEvent)).IsAssignableFrom(type))
                    {
                        eventTypes.Add(type);
                    }
                }

            }
            ListPool<Type[]>.Release(assemblyTypesToScan);
            EventTypes = eventTypes;

            List<Type> staticEventBusesTypes = new List<Type>();
            var typedef = typeof(EventBus<>);
            for (int i = 0; i < EventTypes.Count; i++)
            {
                var type = EventTypes[i];
                var gentype = typedef.MakeGenericType(type);
                staticEventBusesTypes.Add(gentype);
            }

            StaticEventBusesTypes = staticEventBusesTypes;
        }

        public static void ClearAllBuses()
        {
            if(StaticEventBusesTypes == null) StaticEventBusesTypes = new List<Type>();
            for (int i = 0; i < StaticEventBusesTypes.Count; i++)
            {
                var type = StaticEventBusesTypes[i];
                var clearMethod = type.GetMethod("Clear", BindingFlags.Static | BindingFlags.NonPublic);
                clearMethod?.Invoke(null, null);
            }
        }
    }

    public static class EventBusHelper
    {
        public static void Raise(IEvent ev)
        {
#if UNITY_EDITOR
            if (EventBusUtility.PlayModeState == PlayModeStateChange.ExitingPlayMode)
                return;
#endif
            if (ev == null)
                return;
        
            var eventType = ev.GetType();
            var genericBusType = typeof(EventBus<>).MakeGenericType(eventType);
            var raiseMethod = genericBusType.GetMethod("Raise", new[] { eventType });
            raiseMethod?.Invoke(null, new object[] { ev });
        }
    }
    
    internal struct EventBindingMutation<T> where T : struct, IEvent
    {
        public enum MutationType{ Register, Unregister }
        public MutationType Type;
        public EventBinding<T> Binding;
        
        public static EventBindingMutation<T> CreateRegister(EventBinding<T> binding)
        {
            return new EventBindingMutation<T>() { Type = MutationType.Register, Binding = binding };
        }
        
        public static EventBindingMutation<T> CreateUnregister(EventBinding<T> binding)
        {
            return new EventBindingMutation<T>() { Type = MutationType.Unregister, Binding = binding };
        }
    }
    
    public static class EventBus<T> where T : struct, IEvent
    {
        private static List<EventBinding<T>> bindings = new (64);
        private static List<Callback> callbacks = new();
        
        private static uint raiseStackDepth = 0;
        private static List<EventBindingMutation<T>> mutations = new ();

        public class Awaiter : EventBinding<T>
        {
            public bool EventRaised { get; private set; }
            public T Payload { get; private set; }

            public Awaiter() : base((Action)null)
            {
                ((IEventBindingInternal<T>)this).OnEvent = OnEvent;
            }

            private void OnEvent(T ev)
            {
                EventRaised = true;
                Payload = ev;
            }
        }
        
        private struct Callback
        {
            public Action onEventNoArg;
            public Action<T> onEvent;
        }

        // only called when editor state changes 
        private static void Clear()
        {
            bindings.Clear();
            if (bindings.Capacity > 64)
                bindings.Capacity = 64; // allocates new array
            callbacks.Clear();
        }

        public static void Register(EventBinding<T> binding)
        {
            if (binding.Registered)
                return;
            
            if(raiseStackDepth > 0)
            {
                mutations.Add(EventBindingMutation<T>.CreateRegister(binding));
                return;
            }
            binding.InternalIndex = bindings.Count;
            bindings.Add(binding);
        }
        
        private static void SwapRemoveBindingAt(int index)
        {
            int lastIndex = bindings.Count - 1;
            var removed = bindings[index];

            if (index == lastIndex)
            {
                bindings.RemoveAt(lastIndex);
            }
            else
            {
                var moved = bindings[lastIndex];
                bindings[index] = moved;
                bindings.RemoveAt(lastIndex);
                moved.InternalIndex = index;
            }

            removed.InternalIndex = -1;
        }

        public static void AddCallback(Action callback)
        {
            if (callback == null) return;
            callbacks.Add(new Callback() { onEventNoArg = callback });
        }

        public static void AddCallback(Action<T> callback)
        {
            if (callback == null) return;
            callbacks.Add(new Callback() { onEvent = callback });
        }

        public static void Unregister(EventBinding<T> binding)
        {
#if UNITY_EDITOR
            if (EventBusUtility.PlayModeState == PlayModeStateChange.ExitingPlayMode)
                return;
#endif
            int index = binding.InternalIndex;
            if (index == -1 || index > bindings.Count) return; // binding invalid
            if (bindings[index] != binding) return; // binding invalid

            if (raiseStackDepth > 0)
                mutations.Add(EventBindingMutation<T>.CreateUnregister(binding));
            else
                SwapRemoveBindingAt(index);
        }

        public static void Raise(){ Raise(default); }

        public static void Raise(T ev)
        {
#if UNITY_EDITOR
            if (EventBusUtility.PlayModeState == PlayModeStateChange.ExitingPlayMode)
                return;
#endif
            raiseStackDepth++;
            try
            {
                for (int i = 0, n = bindings.Count; i < n; i++)
                {
                    var internalBind = bindings[i];
                    internalBind.OnEvent?.Invoke(ev);
                    internalBind.OnEventArgs?.Invoke();
                }
            }
            finally
            {
                raiseStackDepth--;
                if (raiseStackDepth == 0)
                {
                    foreach (var mutation in mutations)
                    {
                        if (mutation.Type == EventBindingMutation<T>.MutationType.Register)
                            Register(mutation.Binding);
                        else if (mutation.Type == EventBindingMutation<T>.MutationType.Unregister) 
                            Unregister(mutation.Binding);
                    }
                    mutations.Clear();
                }
            }

            for (int i = 0, n = callbacks.Count; i < n; i++)
            {
                Callback cb = callbacks[i];
                cb.onEvent?.Invoke(ev);
                cb.onEventNoArg?.Invoke();
            }
            callbacks.Clear();
        }

        public static string GetDebugInfoString()
        {
            return "Bindings: " + bindings.Count + " BufferSize: " + bindings.Capacity + "\n"
                + "Callbacks: " + callbacks.Count;
        }

        /// <summary>
        /// Allocates an Awaiter : EventBinding<T>
        /// Use to await event in coroutines
        /// </summary>
        /// <returns></returns>
        public static Awaiter NewAwaiter()
        {
            // TODO: do it non alloc
            return new Awaiter();
        }
    }
}
