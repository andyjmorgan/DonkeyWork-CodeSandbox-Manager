import { useState, useCallback, useRef, useEffect } from 'react'
import { Button } from '@/components/ui/button'
import { Progress } from '@/components/ui/progress'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Plus, Loader2, CheckCircle, XCircle, Server, Zap, Shield, Gauge, Plug, Code } from 'lucide-react'
import type { McpCreationEvent, ContainerReadyEvent, KataContainerInfo, PoolStatusResponse } from '@/types/api'
import { PoolChart } from '@/components/sandbox/PoolChart'

export interface McpCreationInfo {
  podName: string
  createdAt: Date
  elapsedSeconds: number
  events: McpCreationEvent[]
  rawMessages: string[]
  containerInfo?: ContainerReadyEvent['containerInfo']
}

interface McpServerCreatorProps {
  onMcpServerCreated: (podName: string, creationInfo: McpCreationInfo) => void
}

type CreationStatus = 'idle' | 'creating' | 'success' | 'error'

interface CreationState {
  status: CreationStatus
  podName: string | null
  progress: number
  message: string
  events: McpCreationEvent[]
  rawMessages: string[]
  containerInfo?: ContainerReadyEvent['containerInfo']
  elapsedSeconds: number
}

export function McpServerCreator({ onMcpServerCreated }: McpServerCreatorProps) {
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
  const callbackCalledRef = useRef(false)

  // Fetch MCP pool status
  useEffect(() => {
    const fetchPoolStatus = async () => {
      try {
        const response = await fetch('/api/mcp-servers/pool/status')
        if (response.ok) {
          const data = await response.json()
          setPoolStatus(data)
        }
      } catch (error) {
        console.error('Failed to fetch MCP pool status:', error)
      }
    }

    fetchPoolStatus()
    const interval = setInterval(fetchPoolStatus, 10000)
    return () => clearInterval(interval)
  }, [])

  // Quick allocation from warm MCP pool
  const allocateMcpServer = useCallback(async () => {
    callbackCalledRef.current = false
    setState({
      status: 'creating',
      podName: null,
      progress: 50,
      message: 'Allocating MCP server from warm pool...',
      events: [],
      rawMessages: [],
      elapsedSeconds: 0,
    })

    try {
      const startTime = Date.now()
      const response = await fetch('/api/mcp-servers/allocate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userId: `user-${Date.now()}` }),
      })

      const elapsedSeconds = (Date.now() - startTime) / 1000

      if (!response.ok) {
        if (response.status === 503) {
          throw new Error('No warm MCP servers available. Try again in a moment or use Create.')
        }
        throw new Error(`HTTP error! status: ${response.status}`)
      }

      const containerInfo: KataContainerInfo = await response.json()

      setState({
        status: 'success',
        podName: containerInfo.name,
        progress: 100,
        message: `MCP server ${containerInfo.name} allocated!`,
        events: [],
        rawMessages: [],
        elapsedSeconds,
        containerInfo,
      })

      onMcpServerCreated(containerInfo.name, {
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
        message: error instanceof Error ? error.message : 'Failed to allocate MCP server',
        events: [],
        rawMessages: [],
        elapsedSeconds: 0,
      })
    }
  }, [onMcpServerCreated])

  // Create with streaming
  const createMcpServer = useCallback(async () => {
    callbackCalledRef.current = false
    setState({
      status: 'creating',
      podName: null,
      progress: 5,
      message: 'Initializing MCP server creation...',
      events: [],
      rawMessages: [],
      elapsedSeconds: 0,
    })

    try {
      const response = await fetch('/api/mcp-servers/', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'text/event-stream',
        },
        body: JSON.stringify({}),
      })

      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`)
      }

      const reader = response.body?.getReader()
      if (!reader) throw new Error('No response body')

      const decoder = new TextDecoder()
      let buffer = ''

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
              const event = JSON.parse(jsonStr) as McpCreationEvent

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
                  const waitProgress = Math.min(15 + (event.attemptNumber * 5), 75)
                  setState(prev => ({
                    ...prev,
                    progress: waitProgress,
                    message: event.message || `Waiting for pod to be ready (attempt ${event.attemptNumber})...`,
                  }))
                  break
                }
                case 'ready': {
                  if (callbackCalledRef.current) break
                  callbackCalledRef.current = true

                  const currentEvents = state.events
                  const currentRawMessages = state.rawMessages

                  setState(prev => ({
                    ...prev,
                    status: 'success' as const,
                    progress: 100,
                    message: `MCP server ${event.podName} is ready!`,
                    containerInfo: event.containerInfo,
                    elapsedSeconds: event.elapsedSeconds,
                  }))

                  setTimeout(() => {
                    onMcpServerCreated(event.podName, {
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
                case 'mcp_starting':
                  setState(prev => ({
                    ...prev,
                    progress: 85,
                    message: event.message || 'Starting MCP process...',
                  }))
                  break
                case 'mcp_started': {
                  if (callbackCalledRef.current) break
                  callbackCalledRef.current = true

                  const currentEvts = state.events
                  const currentMsgs = state.rawMessages

                  setState(prev => ({
                    ...prev,
                    status: 'success' as const,
                    progress: 100,
                    message: `MCP server ${event.podName} is ready!`,
                    elapsedSeconds: event.elapsedSeconds,
                  }))

                  setTimeout(() => {
                    onMcpServerCreated(event.podName, {
                      podName: event.podName,
                      createdAt: new Date(),
                      elapsedSeconds: event.elapsedSeconds,
                      events: [...currentEvts, event],
                      rawMessages: [...currentMsgs, jsonStr],
                    })
                  }, 0)
                  break
                }
                case 'mcp_start_failed':
                  setState(prev => ({
                    ...prev,
                    status: 'error' as const,
                    progress: 0,
                    message: event.reason || 'MCP process failed to start',
                  }))
                  break
                case 'failed':
                  setState(prev => ({
                    ...prev,
                    status: 'error' as const,
                    progress: 0,
                    message: event.reason || 'Failed to create MCP server',
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
        message: error instanceof Error ? error.message : 'Failed to create MCP server',
      }))
    }
  }, [onMcpServerCreated])

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
        <h2 className="text-3xl font-bold mb-3">MCP Server Manager</h2>
        <p className="text-muted-foreground max-w-2xl mx-auto">
          Allocate on-demand MCP (Model Context Protocol) servers running in isolated Kata VMs.
          Start stdio-to-HTTP bridges, proxy JSON-RPC requests, and manage MCP processes.
        </p>
      </div>

      {/* Pool Status */}
      {poolStatus && state.status === 'idle' && (
        <div className="border border-border rounded-lg p-4 bg-card">
          <div className="flex items-center gap-3 mb-4">
            <div className="p-2 rounded-md bg-primary/20">
              <Gauge className="h-5 w-5 text-primary" />
            </div>
            <div>
              <h3 className="font-semibold">MCP Warm Pool Status</h3>
              <p className="text-sm text-muted-foreground">
                {poolStatus.warm} MCP servers ready for instant allocation
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
                <Plug className="h-5 w-5 text-primary" />
              </div>
              <h3 className="font-semibold">stdio-to-HTTP Bridge</h3>
            </div>
            <p className="text-sm text-muted-foreground">
              Run any MCP stdio server behind an HTTP API. Proxy JSON-RPC requests through the Manager.
            </p>
          </div>

          <div className="border border-border rounded-lg p-4 bg-card">
            <div className="flex items-center gap-3 mb-2">
              <div className="p-2 rounded-md bg-primary/10">
                <Shield className="h-5 w-5 text-primary" />
              </div>
              <h3 className="font-semibold">Long-lived Isolation</h3>
            </div>
            <p className="text-sm text-muted-foreground">
              MCP servers get generous 60m idle and 8h max lifetime. Secure VM-level isolation via Kata.
            </p>
          </div>

          <div className="border border-border rounded-lg p-4 bg-card">
            <div className="flex items-center gap-3 mb-2">
              <div className="p-2 rounded-md bg-primary/10">
                <Code className="h-5 w-5 text-primary" />
              </div>
              <h3 className="font-semibold">Pre-exec Scripts</h3>
            </div>
            <p className="text-sm text-muted-foreground">
              Run setup scripts before launching the MCP server. Install packages, configure environment.
            </p>
          </div>
        </div>
      )}

      {/* API Overview */}
      {state.status === 'idle' && (
        <div className="border border-border rounded-lg p-4 bg-card mb-6">
          <h3 className="font-semibold mb-3 flex items-center gap-2">
            <Server className="h-4 w-4" />
            MCP API Endpoints
          </h3>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-3 text-sm">
            <div className="bg-muted/50 rounded p-3 ring-2 ring-primary/50">
              <code className="text-green-600 dark:text-green-400">POST</code>
              <code className="ml-2">/api/mcp-servers/allocate</code>
              <p className="text-muted-foreground text-xs mt-1">Instant allocation from warm pool</p>
            </div>
            <div className="bg-muted/50 rounded p-3">
              <code className="text-green-600 dark:text-green-400">POST</code>
              <code className="ml-2">/api/mcp-servers/{'{podName}'}/start</code>
              <p className="text-muted-foreground text-xs mt-1">Arm - start MCP process with launch command</p>
            </div>
            <div className="bg-muted/50 rounded p-3">
              <code className="text-green-600 dark:text-green-400">POST</code>
              <code className="ml-2">/api/mcp-servers/{'{podName}'}/proxy</code>
              <p className="text-muted-foreground text-xs mt-1">Proxy JSON-RPC request to MCP server</p>
            </div>
            <div className="bg-muted/50 rounded p-3">
              <code className="text-blue-600 dark:text-blue-400">GET</code>
              <code className="ml-2">/api/mcp-servers/{'{podName}'}/status</code>
              <p className="text-muted-foreground text-xs mt-1">Get MCP process status</p>
            </div>
          </div>
        </div>
      )}

      {/* Get an MCP Server */}
      <div className="border border-border rounded-lg p-4 bg-card">
        <div className={`flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 ${state.status !== 'idle' ? 'mb-4' : ''}`}>
          <div>
            <h3 className="text-lg font-semibold">Get an MCP Server</h3>
            {state.status === 'idle' && (
              <p className="text-sm text-muted-foreground mt-1">
                Allocate from warm pool or create a new one
              </p>
            )}
          </div>
          <div className="flex flex-col sm:flex-row items-stretch sm:items-center gap-2">
            {state.status === 'idle' && (
              <>
                <Button
                  variant="outline"
                  onClick={createMcpServer}
                  size="lg"
                  className="w-full sm:w-auto"
                >
                  <Plus className="h-4 w-4 mr-2" />
                  Create
                </Button>
                <Button
                  onClick={allocateMcpServer}
                  size="lg"
                  className="w-full sm:w-auto"
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
                        <span>MCP server allocated from warm pool in {state.elapsedSeconds.toFixed(2)}s</span>
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
                            {event.eventType === 'ready' && `Container ready in ${event.elapsedSeconds.toFixed(1)}s`}
                            {event.eventType === 'failed' && event.reason}
                            {event.eventType === 'mcp_starting' && event.message}
                            {event.eventType === 'mcp_started' && `MCP process started in ${event.elapsedSeconds.toFixed(1)}s`}
                            {event.eventType === 'mcp_start_failed' && event.reason}
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
