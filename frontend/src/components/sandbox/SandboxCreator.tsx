import { useState, useCallback, useRef } from 'react'
import { Button } from '@/components/ui/button'
import { Progress } from '@/components/ui/progress'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Plus, Loader2, CheckCircle, XCircle, Box, Terminal, Zap, Shield } from 'lucide-react'
import type { ContainerEvent, ContainerReadyEvent } from '@/types/api'

export interface CreationInfo {
  podName: string
  createdAt: Date
  elapsedSeconds: number
  events: ContainerEvent[]
  rawMessages: string[]
  containerInfo?: ContainerReadyEvent['containerInfo']
}

interface SandboxCreatorProps {
  onSandboxCreated: (podName: string, creationInfo: CreationInfo) => void
}

type CreationStatus = 'idle' | 'creating' | 'success' | 'error'

interface CreationState {
  status: CreationStatus
  podName: string | null
  progress: number
  message: string
  events: ContainerEvent[]
  rawMessages: string[]
  containerInfo?: ContainerReadyEvent['containerInfo']
  elapsedSeconds: number
}

export function SandboxCreator({ onSandboxCreated }: SandboxCreatorProps) {
  const [state, setState] = useState<CreationState>({
    status: 'idle',
    podName: null,
    progress: 0,
    message: '',
    events: [],
    rawMessages: [],
    elapsedSeconds: 0,
  })

  // Ref to track if callback has been called for current creation
  const callbackCalledRef = useRef(false)

  const createSandbox = useCallback(async () => {
    callbackCalledRef.current = false
    setState({
      status: 'creating',
      podName: null,
      progress: 5,
      message: 'Initializing sandbox creation...',
      events: [],
      rawMessages: [],
      elapsedSeconds: 0,
    })

    try {
      const response = await fetch('/api/kata', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'text/event-stream',
        },
        body: JSON.stringify({
          waitForReady: true,
        }),
      })

      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`)
      }

      const reader = response.body?.getReader()
      if (!reader) {
        throw new Error('No response body')
      }

      const decoder = new TextDecoder()
      let buffer = ''
      // Track state updates during streaming

      while (true) {
        const { done, value } = await reader.read()
        if (done) break

        buffer += decoder.decode(value, { stream: true })
        const lines = buffer.split('\n')
        buffer = lines.pop() || ''

        for (const line of lines) {
          if (line.startsWith('data: ')) {
            const jsonStr = line.slice(6).trim()
            if (!jsonStr) continue

            setState(prev => ({
              ...prev,
              rawMessages: [...prev.rawMessages, jsonStr],
            }))

            try {
              const event = JSON.parse(jsonStr) as ContainerEvent

              setState(prev => ({
                ...prev,
                events: [...prev.events, event],
              }))

              switch (event.eventType) {
                case 'created':
                  setState(prev => ({
                    ...prev,
                    podName: event.podName,
                    progress: 15,
                    message: `Pod ${event.podName} created, waiting for ready...`,
                  }))
                  break
                case 'waiting': {
                  const waitProgress = Math.min(15 + (event.attemptNumber * 5), 85)
                  setState(prev => ({
                    ...prev,
                    progress: waitProgress,
                    message: event.message || `Waiting for pod to be ready (attempt ${event.attemptNumber})...`,
                  }))
                  break
                }
                case 'ready': {
                  // Prevent duplicate callbacks
                  if (callbackCalledRef.current) break
                  callbackCalledRef.current = true

                  // Capture current state values before setState
                  const currentEvents = state.events
                  const currentRawMessages = state.rawMessages

                  // Update state
                  setState(prev => ({
                    ...prev,
                    status: 'success' as const,
                    progress: 100,
                    message: `Sandbox ${event.podName} is ready!`,
                    containerInfo: event.containerInfo,
                    elapsedSeconds: event.elapsedSeconds,
                    events: [...prev.events, event],
                    rawMessages: [...prev.rawMessages, jsonStr],
                  }))

                  // Call callback outside of setState
                  // This avoids React StrictMode calling it twice
                  setTimeout(() => {
                    onSandboxCreated(event.podName, {
                      podName: event.podName,
                      createdAt: new Date(),
                      elapsedSeconds: event.elapsedSeconds,
                      events: [...currentEvents, event],
                      rawMessages: [...currentRawMessages, jsonStr],
                      containerInfo: event.containerInfo,
                    })
                  }, 0)
                  break
                }
                case 'failed':
                  setState(prev => ({
                    ...prev,
                    status: 'error' as const,
                    progress: 0,
                    message: event.reason || 'Failed to create sandbox',
                  }))
                  break
              }
            } catch (parseError) {
              console.error('Failed to parse event:', parseError)
            }
          }
        }
      }
    } catch (error) {
      setState(prev => ({
        ...prev,
        status: 'error',
        progress: 0,
        message: error instanceof Error ? error.message : 'Failed to create sandbox',
      }))
    }
  }, [onSandboxCreated])

  const reset = () => {
    setState({
      status: 'idle',
      podName: null,
      progress: 0,
      message: '',
      events: [],
      rawMessages: [],
      elapsedSeconds: 0,
    })
  }

  return (
    <div className="space-y-6">
      {/* Hero Section */}
      <div className="text-center py-8">
        <h2 className="text-3xl font-bold mb-3">Kata Sandbox Manager</h2>
        <p className="text-muted-foreground max-w-2xl mx-auto">
          Create isolated, secure sandbox environments powered by Kata Containers.
          Execute commands in real-time with full streaming output.
        </p>
      </div>

      {/* Feature Cards */}
      {state.status === 'idle' && (
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4 mb-8">
          <div className="border border-border rounded-lg p-4 bg-card">
            <div className="flex items-center gap-3 mb-2">
              <div className="p-2 rounded-md bg-primary/10">
                <Shield className="h-5 w-5 text-primary" />
              </div>
              <h3 className="font-semibold">Secure Isolation</h3>
            </div>
            <p className="text-sm text-muted-foreground">
              Each sandbox runs in its own Kata VM with hardware-level isolation for maximum security.
            </p>
          </div>

          <div className="border border-border rounded-lg p-4 bg-card">
            <div className="flex items-center gap-3 mb-2">
              <div className="p-2 rounded-md bg-primary/10">
                <Zap className="h-5 w-5 text-primary" />
              </div>
              <h3 className="font-semibold">Real-time Streaming</h3>
            </div>
            <p className="text-sm text-muted-foreground">
              Stream command output in real-time via SSE. See stdout and stderr as they happen.
            </p>
          </div>

          <div className="border border-border rounded-lg p-4 bg-card">
            <div className="flex items-center gap-3 mb-2">
              <div className="p-2 rounded-md bg-primary/10">
                <Terminal className="h-5 w-5 text-primary" />
              </div>
              <h3 className="font-semibold">Full Shell Access</h3>
            </div>
            <p className="text-sm text-muted-foreground">
              Execute any bash command in your sandbox. Perfect for testing, debugging, and automation.
            </p>
          </div>
        </div>
      )}

      {/* API Overview - Only show in idle state */}
      {state.status === 'idle' && (
        <div className="border border-border rounded-lg p-4 bg-card mb-6">
          <h3 className="font-semibold mb-3 flex items-center gap-2">
            <Box className="h-4 w-4" />
            API Endpoints
          </h3>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-3 text-sm">
            <div className="bg-muted/50 rounded p-3">
              <code className="text-green-600 dark:text-green-400">POST</code>
              <code className="ml-2">/api/kata</code>
              <p className="text-muted-foreground text-xs mt-1">Create a new sandbox (SSE stream)</p>
            </div>
            <div className="bg-muted/50 rounded p-3">
              <code className="text-blue-600 dark:text-blue-400">GET</code>
              <code className="ml-2">/api/kata/{'{id}'}</code>
              <p className="text-muted-foreground text-xs mt-1">Get sandbox details</p>
            </div>
            <div className="bg-muted/50 rounded p-3">
              <code className="text-green-600 dark:text-green-400">POST</code>
              <code className="ml-2">/api/kata/{'{id}'}/execute</code>
              <p className="text-muted-foreground text-xs mt-1">Execute command (SSE stream)</p>
            </div>
            <div className="bg-muted/50 rounded p-3">
              <code className="text-red-600 dark:text-red-400">DELETE</code>
              <code className="ml-2">/api/kata/{'{id}'}</code>
              <p className="text-muted-foreground text-xs mt-1">Delete a sandbox</p>
            </div>
          </div>
        </div>
      )}

      {/* Create Button - Always visible at top */}
      <div className="border border-border rounded-lg p-4 bg-card">
        <div className={`flex items-center justify-between ${state.status !== 'idle' ? 'mb-4' : ''}`}>
          <h3 className="text-lg font-semibold">Create New Sandbox</h3>
          {state.status === 'idle' && (
            <Button onClick={createSandbox} size="lg">
              <Plus className="h-4 w-4 mr-2" />
              Create Sandbox
            </Button>
          )}
          {(state.status === 'success' || state.status === 'error') && (
            <Button variant="outline" onClick={reset}>
              Create Another
            </Button>
          )}
        </div>

        {state.status !== 'idle' && (
          <div className="space-y-4">
            {/* Progress Section */}
            <div className="space-y-2">
              <div className="flex items-center gap-2">
                {state.status === 'creating' && (
                  <Loader2 className="h-4 w-4 animate-spin text-primary" />
                )}
                {state.status === 'success' && (
                  <CheckCircle className="h-4 w-4 text-green-500" />
                )}
                {state.status === 'error' && (
                  <XCircle className="h-4 w-4 text-destructive" />
                )}
                <span className="text-sm">{state.message}</span>
              </div>
              <Progress value={state.progress} className="h-2" />
            </div>

            {/* Tabs for Output and Debug */}
            <Tabs defaultValue="output" className="w-full">
              <TabsList className="w-auto">
                <TabsTrigger value="output">Output</TabsTrigger>
                <TabsTrigger value="debug">Debug</TabsTrigger>
              </TabsList>
              <TabsContent value="output" className="mt-2">
                <div className="bg-muted rounded-md p-3 max-h-48 overflow-y-auto">
                  {state.events.length === 0 ? (
                    <p className="text-sm text-muted-foreground">Waiting for events...</p>
                  ) : (
                    <ul className="space-y-1 text-sm">
                      {state.events.map((event, idx) => (
                        <li key={idx} className="flex items-start gap-2">
                          <span className="text-muted-foreground">[{event.eventType}]</span>
                          <span>
                            {event.eventType === 'created' && `Pod ${event.podName} created (${event.phase})`}
                            {event.eventType === 'waiting' && event.message}
                            {event.eventType === 'ready' && `Ready in ${event.elapsedSeconds.toFixed(1)}s`}
                            {event.eventType === 'failed' && event.reason}
                          </span>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              </TabsContent>
              <TabsContent value="debug" className="mt-2">
                <div className="bg-muted rounded-md p-3 max-h-48 overflow-y-auto terminal-output">
                  {state.rawMessages.length === 0 ? (
                    <p className="text-sm text-muted-foreground">No raw messages yet...</p>
                  ) : (
                    <pre className="text-xs whitespace-pre-wrap break-all">
                      {state.rawMessages.map((msg, idx) => (
                        <div key={idx} className="mb-1">
                          <span className="text-muted-foreground">{idx + 1}:</span> {msg}
                        </div>
                      ))}
                    </pre>
                  )}
                </div>
              </TabsContent>
            </Tabs>
          </div>
        )}
      </div>
    </div>
  )
}
