import { useState, useCallback, useRef, useEffect } from 'react'
import { Button } from '@/components/ui/button'
import { Progress } from '@/components/ui/progress'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Plus, Loader2, CheckCircle, XCircle, Box, Terminal, Zap, Shield, Gauge, ChevronDown, Trash2, AlertTriangle, Eye, EyeOff } from 'lucide-react'
import { Input } from '@/components/ui/input'
import type { ContainerEvent, ContainerReadyEvent, KataContainerInfo, PoolStatusResponse } from '@/types/api'
import { PoolChart } from './PoolChart'

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

  const [poolStatus, setPoolStatus] = useState<PoolStatusResponse | null>(null)
  const [advancedOpen, setAdvancedOpen] = useState(false)
  const [apiKey, setApiKey] = useState('')
  const [showApiKey, setShowApiKey] = useState(false)
  const [deleteStatus, setDeleteStatus] = useState<{
    status: 'idle' | 'deleting' | 'success' | 'error'
    message: string
    deletedCount?: number
    failedCount?: number
  }>({ status: 'idle', message: '' })

  // Ref to track if callback has been called for current creation
  const callbackCalledRef = useRef(false)

  // Fetch pool status on mount and periodically
  useEffect(() => {
    const fetchPoolStatus = async () => {
      try {
        const response = await fetch('/api/kata/pool/status')
        if (response.ok) {
          const data = await response.json()
          setPoolStatus(data)
        }
      } catch (error) {
        console.error('Failed to fetch pool status:', error)
      }
    }

    fetchPoolStatus()
    const interval = setInterval(fetchPoolStatus, 10000) // Refresh every 10s
    return () => clearInterval(interval)
  }, [])

  // Quick allocation from warm pool (default method)
  const allocateSandbox = useCallback(async () => {
    callbackCalledRef.current = false
    setState({
      status: 'creating',
      podName: null,
      progress: 50,
      message: 'Allocating sandbox from warm pool...',
      events: [],
      rawMessages: [],
      elapsedSeconds: 0,
    })

    try {
      const startTime = Date.now()
      const response = await fetch('/api/kata/allocate', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          userId: `user-${Date.now()}`,
        }),
      })

      const elapsedSeconds = (Date.now() - startTime) / 1000

      if (!response.ok) {
        if (response.status === 503) {
          throw new Error('No warm sandboxes available. Try again in a moment or use advanced create.')
        }
        throw new Error(`HTTP error! status: ${response.status}`)
      }

      const containerInfo: KataContainerInfo = await response.json()

      setState({
        status: 'success',
        podName: containerInfo.name,
        progress: 100,
        message: `Sandbox ${containerInfo.name} allocated!`,
        events: [],
        rawMessages: [],
        elapsedSeconds,
        containerInfo,
      })

      // Call parent callback
      onSandboxCreated(containerInfo.name, {
        podName: containerInfo.name,
        createdAt: new Date(),
        elapsedSeconds,
        events: [],
        rawMessages: [],
        containerInfo,
      })
    } catch (error) {
      setState({
        status: 'error',
        podName: null,
        progress: 0,
        message: error instanceof Error ? error.message : 'Failed to allocate sandbox',
        events: [],
        rawMessages: [],
        elapsedSeconds: 0,
      })
    }
  }, [onSandboxCreated])

  // Advanced create with streaming (fallback method)
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

  const deleteAllContainers = async () => {
    if (!apiKey.trim()) {
      setDeleteStatus({
        status: 'error',
        message: 'API key is required',
      })
      return
    }

    setDeleteStatus({ status: 'deleting', message: 'Deleting all containers...' })

    try {
      const response = await fetch('/api/kata', {
        method: 'DELETE',
        headers: {
          'X-Api-Key': apiKey,
        },
      })

      if (!response.ok) {
        if (response.status === 401) {
          throw new Error('Invalid API key')
        }
        throw new Error(`HTTP error! status: ${response.status}`)
      }

      const result = await response.json()
      setDeleteStatus({
        status: 'success',
        message: `Deleted ${result.deletedCount} container(s)`,
        deletedCount: result.deletedCount,
        failedCount: result.failedCount,
      })
    } catch (error) {
      setDeleteStatus({
        status: 'error',
        message: error instanceof Error ? error.message : 'Failed to delete containers',
      })
    }
  }

  return (
    <div className="space-y-6">
      {/* Hero Section */}
      <div className="text-center py-8">
        <h2 className="text-3xl font-bold mb-3">Kata Sandbox Manager</h2>
        <p className="text-muted-foreground max-w-2xl mx-auto">
          Get instant access to pre-warmed sandbox environments powered by Kata Containers.
          Execute commands in real-time with full streaming output.
        </p>
      </div>

      {/* Pool Status Banner with Chart */}
      {poolStatus && state.status === 'idle' && (
        <div className="border border-border rounded-lg p-4 bg-card">
          <div className="flex items-center gap-3 mb-4">
            <div className="p-2 rounded-md bg-primary/20">
              <Gauge className="h-5 w-5 text-primary" />
            </div>
            <div>
              <h3 className="font-semibold">Warm Pool Status</h3>
              <p className="text-sm text-muted-foreground">
                {poolStatus.warm} sandboxes ready for instant allocation
              </p>
            </div>
          </div>
          <PoolChart poolStatus={poolStatus} />
        </div>
      )}

      {/* Feature Cards */}
      {state.status === 'idle' && (
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4 mb-8">
          <div className="border border-border rounded-lg p-4 bg-card">
            <div className="flex items-center gap-3 mb-2">
              <div className="p-2 rounded-md bg-primary/10">
                <Zap className="h-5 w-5 text-primary" />
              </div>
              <h3 className="font-semibold">Instant Allocation</h3>
            </div>
            <p className="text-sm text-muted-foreground">
              Get a pre-warmed sandbox in milliseconds. No waiting for pod creation or image pulls.
            </p>
          </div>

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
                <Terminal className="h-5 w-5 text-primary" />
              </div>
              <h3 className="font-semibold">Real-time Streaming</h3>
            </div>
            <p className="text-sm text-muted-foreground">
              Stream command output via SSE. See stdout and stderr as they happen with exit codes.
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
            <div className="bg-muted/50 rounded p-3 ring-2 ring-primary/50">
              <code className="text-green-600 dark:text-green-400">POST</code>
              <code className="ml-2">/api/kata/allocate</code>
              <p className="text-muted-foreground text-xs mt-1">⚡ Instant allocation from warm pool</p>
            </div>
            <div className="bg-muted/50 rounded p-3">
              <code className="text-blue-600 dark:text-blue-400">GET</code>
              <code className="ml-2">/api/kata/pool/status</code>
              <p className="text-muted-foreground text-xs mt-1">Get pool status</p>
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
          <div>
            <h3 className="text-lg font-semibold">Get a Sandbox</h3>
            {state.status === 'idle' && (
              <p className="text-sm text-muted-foreground mt-1">
                Allocate from warm pool or create a new one
              </p>
            )}
          </div>
          <div className="flex items-center gap-2">
            {state.status === 'idle' && (
              <>
                <Button
                  variant="outline"
                  onClick={createSandbox}
                  size="lg"
                >
                  <Plus className="h-4 w-4 mr-2" />
                  Create
                </Button>
                <Button
                  onClick={allocateSandbox}
                  size="lg"
                >
                  <Zap className="h-4 w-4 mr-2" />
                  Allocate
                </Button>
              </>
            )}
            {(state.status === 'success' || state.status === 'error') && (
              <Button variant="outline" onClick={reset}>
                Get Another
              </Button>
            )}
          </div>
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
                  {state.events.length === 0 && state.status === 'success' ? (
                    <div className="text-sm">
                      <div className="flex items-center gap-2 text-green-600 dark:text-green-400">
                        <CheckCircle className="h-4 w-4" />
                        <span>Sandbox allocated from warm pool in {state.elapsedSeconds.toFixed(2)}s</span>
                      </div>
                      {state.containerInfo && (
                        <div className="mt-2 space-y-1 text-muted-foreground">
                          <div>Pod: {state.containerInfo.name}</div>
                          <div>IP: {state.containerInfo.podIP}</div>
                          <div>Node: {state.containerInfo.nodeName || 'N/A'}</div>
                        </div>
                      )}
                    </div>
                  ) : state.events.length === 0 ? (
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

      {/* Advanced Section - Collapsible */}
      {state.status === 'idle' && (
        <div className="border border-border rounded-lg bg-card">
          <button
            onClick={() => setAdvancedOpen(!advancedOpen)}
            className="w-full flex items-center justify-between p-4 text-left hover:bg-muted/50 transition-colors"
          >
            <div className="flex items-center gap-2">
              <AlertTriangle className="h-4 w-4 text-orange-500" />
              <span className="font-semibold">Advanced</span>
            </div>
            <ChevronDown className={`h-4 w-4 transition-transform ${advancedOpen ? 'rotate-180' : ''}`} />
          </button>

          {advancedOpen && (
            <div className="px-4 pb-4 border-t border-border pt-4">
              <div className="space-y-4">
                <div className="bg-destructive/10 border border-destructive/20 rounded-lg p-4">
                  <div className="flex items-start gap-3">
                    <Trash2 className="h-5 w-5 text-destructive mt-0.5" />
                    <div className="flex-1">
                      <h4 className="font-semibold text-destructive">Delete All Containers</h4>
                      <p className="text-sm text-muted-foreground mt-1">
                        This will delete all sandbox containers in the namespace. This action cannot be undone.
                      </p>

                      <div className="mt-4 space-y-3">
                        <div>
                          <label htmlFor="apiKey" className="text-sm font-medium">
                            API Key <span className="text-destructive">*</span>
                          </label>
                          <div className="relative mt-1">
                            <Input
                              id="apiKey"
                              type={showApiKey ? 'text' : 'password'}
                              placeholder="Enter API key to authorize"
                              value={apiKey}
                              onChange={(e) => setApiKey(e.target.value)}
                              className="pr-10"
                            />
                            <button
                              type="button"
                              onClick={() => setShowApiKey(!showApiKey)}
                              className="absolute right-2 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground transition-colors"
                            >
                              {showApiKey ? (
                                <EyeOff className="h-4 w-4" />
                              ) : (
                                <Eye className="h-4 w-4" />
                              )}
                            </button>
                          </div>
                        </div>

                        <Button
                          variant="destructive"
                          onClick={deleteAllContainers}
                          disabled={deleteStatus.status === 'deleting' || !apiKey.trim()}
                        >
                          {deleteStatus.status === 'deleting' ? (
                            <>
                              <Loader2 className="h-4 w-4 mr-2 animate-spin" />
                              Deleting...
                            </>
                          ) : (
                            <>
                              <Trash2 className="h-4 w-4 mr-2" />
                              Delete All Containers
                            </>
                          )}
                        </Button>

                        {deleteStatus.status === 'success' && (
                          <div className="flex items-center gap-2 text-green-600 dark:text-green-400 text-sm">
                            <CheckCircle className="h-4 w-4" />
                            <span>{deleteStatus.message}</span>
                            {deleteStatus.failedCount && deleteStatus.failedCount > 0 && (
                              <span className="text-orange-500">({deleteStatus.failedCount} failed)</span>
                            )}
                          </div>
                        )}

                        {deleteStatus.status === 'error' && (
                          <div className="flex items-center gap-2 text-destructive text-sm">
                            <XCircle className="h-4 w-4" />
                            <span>{deleteStatus.message}</span>
                          </div>
                        )}
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
