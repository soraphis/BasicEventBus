namespace EventBus
{
    using System;
    using System.Collections.Generic;
    using EventBus.Internal;

    namespace Internal
    {
        internal interface IEventBindingInternal<T> where T : struct, IEvent
        {
            public Action<T> OnEvent { get; set; }
            public Action OnEventArgs { get; set; }
        }
    }

    public class EventBinding<T> : IEventBindingInternal<T>, IDisposable 
        where T : struct, IEvent
    {
        public int InternalIndex { get; set; } = -1;

        public bool Registered => InternalIndex != -1;

        private bool _listen;

        private Action<T> _onEvent;
        private Action _onEventNoArgs;

        Action<T> IEventBindingInternal<T>.OnEvent { get => _onEvent; set => _onEvent = value; }
        Action IEventBindingInternal<T>.OnEventArgs { get => _onEventNoArgs; set => _onEventNoArgs = value; }
        
        internal Action<T> OnEvent => _onEvent;
        internal Action OnEventArgs => _onEventNoArgs;

        internal EventBinding(Action<T> onEvent)
        {
            this._onEvent = onEvent;
            Resume();
        }

        internal EventBinding(Action onEventNoArgs)
        {
            this._onEventNoArgs = onEventNoArgs;
            Resume();
        }

        #region PublicAPI

        public static EventBinding<T> Subscribe(Action<T> onEvent) => new(onEvent);
        public static EventBinding<T> Subscribe(Action onEventNoArgs) => new(onEventNoArgs);
        public void Dispose() { SetListen(false); _onEvent = null; _onEventNoArgs = null; }
        public void Pause() => SetListen(false);
        public void Resume() => SetListen(true);

        public void Add(Action<T> onEvent) => _onEvent += onEvent;
        public void Remove(Action<T> onEvent) => _onEvent -= onEvent;

        public void Add(Action onEvent) => _onEventNoArgs += onEvent;
        public void Remove(Action onEvent) => _onEventNoArgs -= onEvent;
        
        #endregion

        private void SetListen(bool value)
        {
            if (value == _listen)
                return;

            if (value)
                EventBus<T>.Register(this);
            else
                EventBus<T>.Unregister(this);

            _listen = value;
        }

        public static implicit operator EventBinding<T>(Action onEventNoArgs)
        {
            return new EventBinding<T>(onEventNoArgs);
        }

        public static implicit operator EventBinding<T>(Action<T> onEvent)
        {
            return new EventBinding<T>(onEvent);
        }

        public static implicit operator bool(EventBinding<T> bind)
        {
            return bind != null;
        }

    }
}