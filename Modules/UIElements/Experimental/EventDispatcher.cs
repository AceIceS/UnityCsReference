// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;

namespace UnityEngine.Experimental.UIElements
{
    // value that determines if a event handler stops propagation of events or allows it to continue.
    // TODO: [Obsolete("Call EventBase.StopPropagation() instead of using EventPropagation.Stop.")]
    public enum  EventPropagation
    {
        // continue event propagation after this handler
        Continue,
        // stop event propagation after this handler
        Stop
    }

    // With the following VisualElement tree existing
    // root
    //  container A
    //      button B
    //      Textfield C with KeyboardFocus  <-- Event 2 Key Down A
    //  container D
    //      container E
    //          button F  <-- Event 1 Click
    //
    // For example: In the case of Event 1 Button F getting clicked, the following handlers will be called if registered:
    // result ==> Phase TrickleDown [ root, D, E ], Phase Target [F], Phase BubbleUp [ E, D, root ]
    //
    // For example 2: A keydown with Textfocus in TextField C
    // result ==> Phase TrickleDown [ root, A], Phase Target [C], Phase BubbleUp [ A, root ]

    enum DispatchMode
    {
        Default = Queued,
        Queued = 1,
        Immediate = 2,
    }

    public sealed class EventDispatcher
    {
        public struct Gate : IDisposable
        {
            EventDispatcher m_Dispatcher;

            public Gate(EventDispatcher d)
            {
                m_Dispatcher = d;
                m_Dispatcher.CloseGate();
            }

            public void Dispose()
            {
                m_Dispatcher.OpenGate();
            }
        }

        struct EventRecord
        {
            public EventBase m_Event;
            public IPanel m_Panel;
        }

        List<IEventDispatchingStrategy> m_DispatchingStrategies;
        static readonly ObjectPool<Queue<EventRecord>> k_EventQueuePool = new ObjectPool<Queue<EventRecord>>();
        Queue<EventRecord> m_Queue;

        uint m_GateCount;

        struct DispatchContext
        {
            public uint m_GateCount;
            public Queue<EventRecord> m_Queue;
        }

        Stack<DispatchContext> m_DispatchContexts = new Stack<DispatchContext>();

        static EventDispatcher s_EventDispatcher;

        internal static EventDispatcher instance
        {
            get
            {
                if (s_EventDispatcher == null)
                    s_EventDispatcher = new EventDispatcher();

                return s_EventDispatcher;
            }
        }

        internal static void ClearDispatcher()
        {
            s_EventDispatcher = null;
        }

        EventDispatcher()
        {
            m_DispatchingStrategies = new List<IEventDispatchingStrategy>();
            m_DispatchingStrategies.Add(new DebuggerEventDispatchingStrategy());
            m_DispatchingStrategies.Add(new MouseCaptureDispatchingStrategy());
            m_DispatchingStrategies.Add(new KeyboardEventDispatchingStrategy());
            m_DispatchingStrategies.Add(new MouseEventDispatchingStrategy());
            m_DispatchingStrategies.Add(new CommandEventDispatchingStrategy());
            m_DispatchingStrategies.Add(new IMGUIEventDispatchingStrategy());
            m_DispatchingStrategies.Add(new DefaultDispatchingStrategy());

            m_Queue = k_EventQueuePool.Get();
        }

        bool m_Immediate = false;
        bool dispatchImmediately
        {
            get { return m_Immediate || m_GateCount == 0; }
        }

        internal void Dispatch(EventBase evt, IPanel panel, DispatchMode dispatchMode)
        {
            evt.MarkReceivedByDispatcher();

            if (evt.GetEventTypeId() == IMGUIEvent.TypeId())
            {
                Event e = evt.imguiEvent;
                if (e.type == EventType.Repaint)
                {
                    return;
                }
            }

            if (dispatchImmediately || (dispatchMode == DispatchMode.Immediate))
            {
                ProcessEvent(evt, panel);
            }
            else
            {
                evt.Acquire();
                m_Queue.Enqueue(new EventRecord {m_Event = evt, m_Panel = panel});
            }
        }

        internal void PushDispatcherContext()
        {
            m_DispatchContexts.Push(new DispatchContext() {m_GateCount = m_GateCount, m_Queue = m_Queue});
            m_GateCount = 0;
            m_Queue = k_EventQueuePool.Get();
        }

        internal void PopDispatcherContext()
        {
            Debug.Assert(m_GateCount == 0, "All gates should have been opened before popping dispatch context.");
            Debug.Assert(m_Queue.Count == 0, "Queue should be empty when popping dispatch context.");

            k_EventQueuePool.Release(m_Queue);

            m_GateCount = m_DispatchContexts.Peek().m_GateCount;
            m_Queue = m_DispatchContexts.Peek().m_Queue;
            m_DispatchContexts.Pop();
        }

        internal void CloseGate()
        {
            m_GateCount++;
        }

        internal void OpenGate()
        {
            Debug.Assert(m_GateCount > 0);

            if (m_GateCount > 0)
            {
                m_GateCount--;
            }

            if (m_GateCount == 0)
            {
                ProcessEventQueue();
            }
        }

        void ProcessEventQueue()
        {
            // While processing the current queue, we need a new queue to store additional events that
            // might be generated during current queue events processing. Thanks to the gate mechanism,
            // events put in the new queue will be processed before the remaining events in the current
            // queue (but after processing of the event generating them is completed).
            //
            // For example, MouseDownEvent generates FocusOut, FocusIn, Blur and Focus events. And let's
            // say that FocusIn generates ValueChanged and GeometryChanged events.
            //
            // Without queue swapping, order of event processing would be MouseDown, FocusOut, FocusIn,
            // Blur, Focus, ValueChanged, GeometryChanged. It is not the same as order of event emission.
            //
            // With queue swapping, order is MouseDown, FocusOut, FocusIn, ValueChanged, GeometryChanged,
            // Blur, Focus. This preserve the order of event emission, and each event is completely
            // processed before processing the next event.

            Queue<EventRecord> queueToProcess = m_Queue;
            m_Queue = k_EventQueuePool.Get();

            ExitGUIException caughtExitGUIException = null;

            try
            {
                while (queueToProcess.Count > 0)
                {
                    EventRecord eventRecord = queueToProcess.Dequeue();
                    EventBase evt = eventRecord.m_Event;
                    IPanel panel = eventRecord.m_Panel;
                    try
                    {
                        ProcessEvent(evt, panel);
                    }
                    catch (ExitGUIException e)
                    {
                        Debug.Assert(caughtExitGUIException == null);
                        caughtExitGUIException = e;
                    }
                    finally
                    {
                        evt.Dispose();
                    }
                }
            }
            finally
            {
                k_EventQueuePool.Release(queueToProcess);
            }

            if (caughtExitGUIException != null)
            {
                throw caughtExitGUIException;
            }
        }

        void ProcessEvent(EventBase evt, IPanel panel)
        {
            Event e = evt.imguiEvent;
            // Sometimes (in tests only?) we receive Used events. Protect our verification from this case.
            bool imguiEventIsInitiallyUsed = e != null && e.type == EventType.Used;

            using (new Gate(this))
            {
                evt.PreDispatch();

                IMouseEvent mouseEvent = evt as IMouseEvent;
                IMouseEventInternal mouseEventInternal = evt as IMouseEventInternal;
                if (mouseEvent != null && mouseEventInternal != null && mouseEventInternal.triggeredByOS)
                {
                    MousePositionTracker.SaveMousePosition(mouseEvent.mousePosition, panel);
                }

                foreach (var strategy in m_DispatchingStrategies)
                {
                    if (strategy.CanDispatchEvent(evt))
                    {
                        strategy.DispatchEvent(evt, panel);

                        Debug.Assert(imguiEventIsInitiallyUsed || evt.isPropagationStopped || e == null || e.type != EventType.Used,
                            "Unexpected condition: !evt.isPropagationStopped && evt.imguiEvent.type == EventType.Used.");

                        if (evt.stopDispatch || evt.isPropagationStopped)
                            break;
                    }
                }

                EventDispatchUtilities.ExecuteDefaultAction(evt, panel);
                evt.PostDispatch();

                Debug.Assert(imguiEventIsInitiallyUsed || evt.isPropagationStopped || e == null || e.type != EventType.Used, "Event is used but not stopped.");
            }
        }
    }
}