import { useState, useCallback, useEffect } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Server, Loader2, Trash2, Play, Square, Search, Send, ChevronDown, ChevronRight, CheckCircle, XCircle } from 'lucide-react'
import type { McpStatusResponse } from '@/types/api'
import type { McpCreationInfo } from './McpServerCreator'
import { cn } from '@/lib/utils'

interface McpServerDetailProps {
  serverId: string
  creationInfo: McpCreationInfo
  onDelete?: () => void
}

interface ProxyExecution {
  id: string
  requestBody: string
  responseBody: string | null
  status: 'sending' | 'completed' | 'error'
  startedAt: Date
  isExpanded: boolean
}

export function McpServerDetail({ serverId, creationInfo, onDelete }: McpServerDetailProps) {
  const [mcpStatus, setMcpStatus] = useState<McpStatusResponse | null>(null)
  const [isLoadingStatus, setIsLoadingStatus] = useState(false)

  // Start (arm) form state
  const [command, setCommand] = useState('')
  const [commandArgs, setCommandArgs] = useState('')
  const [preExecScripts, setPreExecScripts] = useState('')
  const [timeoutSeconds, setTimeoutSeconds] = useState(30)
  const [isStarting, setIsStarting] = useState(false)
  const [startError, setStartError] = useState<string | null>(null)
  const [isStopping, setIsStopping] = useState(false)

  // Proxy state
  const [proxyBody, setProxyBody] = useState('{\n  "jsonrpc": "2.0",\n  "method": "tools/list",\n  "id": 1\n}')
  const [proxyExecutions, setProxyExecutions] = useState<ProxyExecution[]>([])
  const [isSending, setIsSending] = useState(false)

  // Poll MCP status
  const fetchStatus = useCallback(async () => {
    setIsLoadingStatus(true)
    try {
      const response = await fetch(`/api/mcp-servers/${serverId}/status`)
      if (response.ok) {
        const data = await response.json()
        setMcpStatus(data)
      }
    } catch (error) {
      console.error('Failed to fetch MCP status:', error)
    } finally {
      setIsLoadingStatus(false)
    }
  }, [serverId])

  useEffect(() => {
    fetchStatus()
    const interval = setInterval(fetchStatus, 5000)
    return () => clearInterval(interval)
  }, [fetchStatus])

  const startMcpProcess = async () => {
    if (!command.trim()) return
    setIsStarting(true)
    setStartError(null)

    try {
      // Parse arguments - split by whitespace but respect quoted strings
      const parseArgs = (str: string): string[] => {
        if (!str.trim()) return []
        const args: string[] = []
        let current = ''
        let inQuote = false
        let quoteChar = ''
        for (const char of str) {
          if ((char === '"' || char === "'") && !inQuote) {
            inQuote = true
            quoteChar = char
          } else if (char === quoteChar && inQuote) {
            inQuote = false
            quoteChar = ''
          } else if (char === ' ' && !inQuote) {
            if (current) {
              args.push(current)
              current = ''
            }
          } else {
            current += char
          }
        }
        if (current) args.push(current)
        return args
      }

      const response = await fetch(`/api/mcp-servers/${serverId}/start`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          command: command.trim(),
          arguments: parseArgs(commandArgs),
          preExecScripts: preExecScripts.trim()
            ? preExecScripts.trim().split('\n').filter(s => s.trim())
            : [],
          timeoutSeconds,
        }),
      })

      if (!response.ok) {
        const errorText = await response.text()
        throw new Error(errorText || `HTTP ${response.status}`)
      }

      await fetchStatus()
    } catch (error) {
      setStartError(error instanceof Error ? error.message : 'Failed to start MCP process')
    } finally {
      setIsStarting(false)
    }
  }

  const stopMcpProcess = async () => {
    setIsStopping(true)
    try {
      const response = await fetch(`/api/mcp-servers/${serverId}/process`, {
        method: 'DELETE',
      })
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      await fetchStatus()
    } catch (error) {
      alert(error instanceof Error ? error.message : 'Failed to stop MCP process')
    } finally {
      setIsStopping(false)
    }
  }

  const deleteMcpServer = async () => {
    if (!confirm(`Are you sure you want to delete MCP server ${serverId}?`)) return
    try {
      const response = await fetch(`/api/mcp-servers/${serverId}`, { method: 'DELETE' })
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      onDelete?.()
    } catch (error) {
      alert(error instanceof Error ? error.message : 'Failed to delete MCP server')
    }
  }

  const sendProxyRequest = async () => {
    if (!proxyBody.trim() || isSending) return

    const executionId = `proxy-${Date.now()}`
    const newExec: ProxyExecution = {
      id: executionId,
      requestBody: proxyBody.trim(),
      responseBody: null,
      status: 'sending',
      startedAt: new Date(),
      isExpanded: true,
    }

    setProxyExecutions(prev => [newExec, ...prev])
    setIsSending(true)

    try {
      const response = await fetch(`/api/mcp-servers/${serverId}/proxy`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: proxyBody.trim(),
      })

      const responseText = await response.text()

      let formattedResponse: string
      try {
        formattedResponse = JSON.stringify(JSON.parse(responseText), null, 2)
      } catch {
        formattedResponse = responseText
      }

      setProxyExecutions(prev => prev.map(exec =>
        exec.id === executionId
          ? { ...exec, responseBody: formattedResponse, status: response.ok ? 'completed' : 'error' }
          : exec
      ))
    } catch (error) {
      setProxyExecutions(prev => prev.map(exec =>
        exec.id === executionId
          ? { ...exec, responseBody: error instanceof Error ? error.message : 'Request failed', status: 'error' }
          : exec
      ))
    } finally {
      setIsSending(false)
    }
  }

  const toggleProxyExpanded = (id: string) => {
    setProxyExecutions(prev => prev.map(exec =>
      exec.id === id ? { ...exec, isExpanded: !exec.isExpanded } : exec
    ))
  }

  const isReady = mcpStatus?.state === 'Ready'
  const isIdle = mcpStatus?.state === 'Idle'
  const needsArming = isIdle || !mcpStatus

  const statusColor = (state?: string) => {
    switch (state) {
      case 'Ready': return 'bg-green-500/20 text-green-600 dark:text-green-400'
      case 'Idle': return 'bg-yellow-500/20 text-yellow-600 dark:text-yellow-400'
      case 'Initializing': return 'bg-blue-500/20 text-blue-600 dark:text-blue-400'
      case 'Error': return 'bg-red-500/20 text-red-600 dark:text-red-400'
      case 'Disposed': return 'bg-gray-500/20 text-gray-600 dark:text-gray-400'
      default: return 'bg-gray-500/20 text-gray-600 dark:text-gray-400'
    }
  }

  return (
    <div className="flex flex-col h-full space-y-4">
      {/* Header */}
      <div className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 min-w-0">
          <Server className="h-5 w-5 flex-shrink-0" />
          <span className="font-medium hidden sm:inline">MCP Server:</span>
          <code className="text-sm bg-muted px-2 py-1 rounded truncate">{serverId}</code>
          {mcpStatus && (
            <span className={cn("text-xs px-2 py-1 rounded", statusColor(mcpStatus.state))}>
              {mcpStatus.state}
            </span>
          )}
          {isLoadingStatus && <Loader2 className="h-3 w-3 animate-spin text-muted-foreground" />}
        </div>
        <div className="flex items-center gap-2 flex-shrink-0">
          <Button variant="outline" size="sm" onClick={fetchStatus} title="Refresh Status">
            <Search className="h-4 w-4" />
            <span className="hidden sm:inline ml-1">Status</span>
          </Button>
          {isReady && (
            <Button variant="outline" size="sm" onClick={stopMcpProcess} disabled={isStopping} title="Stop Process">
              {isStopping ? <Loader2 className="h-4 w-4 animate-spin" /> : <Square className="h-4 w-4" />}
              <span className="hidden sm:inline ml-1">Stop</span>
            </Button>
          )}
          <Button variant="destructive" size="sm" onClick={deleteMcpServer} title="Delete Server">
            <Trash2 className="h-4 w-4" />
            <span className="hidden sm:inline ml-1">Delete</span>
          </Button>
        </div>
      </div>

      {/* Status Panel */}
      {mcpStatus && (
        <div className="border border-border rounded-lg p-4 bg-card">
          <div className="grid grid-cols-2 md:grid-cols-4 gap-3 text-sm">
            <div>
              <span className="text-muted-foreground block text-xs">State</span>
              <span className={cn("text-xs px-2 py-1 rounded inline-block mt-1", statusColor(mcpStatus.state))}>
                {mcpStatus.state}
              </span>
            </div>
            <div>
              <span className="text-muted-foreground block text-xs">Started At</span>
              <span>{mcpStatus.startedAt ? new Date(mcpStatus.startedAt).toLocaleTimeString() : 'N/A'}</span>
            </div>
            <div>
              <span className="text-muted-foreground block text-xs">Last Request</span>
              <span>{mcpStatus.lastRequestAt ? new Date(mcpStatus.lastRequestAt).toLocaleTimeString() : 'N/A'}</span>
            </div>
            <div>
              <span className="text-muted-foreground block text-xs">Error</span>
              <span className="text-red-500">{mcpStatus.error || 'None'}</span>
            </div>
          </div>
        </div>
      )}

      {/* Main Content - Tabs */}
      <Tabs defaultValue={needsArming ? 'start' : 'proxy'} className="flex-1 flex flex-col">
        <TabsList className="w-auto">
          <TabsTrigger value="start">Start / Arm</TabsTrigger>
          <TabsTrigger value="proxy">JSON-RPC Proxy</TabsTrigger>
          <TabsTrigger value="info">Info</TabsTrigger>
        </TabsList>

        {/* Start / Arm Tab */}
        <TabsContent value="start" className="flex-1 mt-2">
          <div className="border border-border rounded-lg p-4 bg-card space-y-4">
            <div>
              <h4 className="font-semibold mb-1">Start MCP Process</h4>
              <p className="text-sm text-muted-foreground">
                Provide a launch command to start the MCP stdio server inside the container.
              </p>
            </div>

            <div className="space-y-3">
              <div>
                <label className="text-sm font-medium">
                  Command <span className="text-destructive">*</span>
                </label>
                <Input
                  value={command}
                  onChange={(e) => setCommand(e.target.value)}
                  placeholder="npx"
                  className="mt-1 font-mono"
                />
              </div>

              <div>
                <label className="text-sm font-medium">Arguments</label>
                <Input
                  value={commandArgs}
                  onChange={(e) => setCommandArgs(e.target.value)}
                  placeholder="-y @modelcontextprotocol/server-filesystem /home/user"
                  className="mt-1 font-mono"
                />
                <div className="flex flex-wrap gap-2 mt-2">
                  <Button
                    variant="outline"
                    size="sm"
                    className="text-xs"
                    onClick={() => {
                      setCommand('npx')
                      setCommandArgs('-y @modelcontextprotocol/server-filesystem /home/user')
                    }}
                  >
                    Filesystem
                  </Button>
                  <Button
                    variant="outline"
                    size="sm"
                    className="text-xs"
                    onClick={() => {
                      setCommand('npx')
                      setCommandArgs('-y @modelcontextprotocol/server-everything')
                    }}
                  >
                    Everything
                  </Button>
                </div>
              </div>

              <div>
                <label className="text-sm font-medium">Pre-exec Scripts (one per line, optional)</label>
                <Textarea
                  value={preExecScripts}
                  onChange={(e) => setPreExecScripts(e.target.value)}
                  placeholder="npm install -g some-package&#10;echo 'Setup complete'"
                  className="mt-1 font-mono min-h-[60px]"
                  rows={2}
                />
              </div>

              <div className="flex items-center gap-2">
                <label className="text-sm font-medium">Timeout:</label>
                <Input
                  type="number"
                  value={timeoutSeconds}
                  onChange={(e) => setTimeoutSeconds(Math.max(5, Math.min(300, parseInt(e.target.value) || 30)))}
                  className="w-20 h-8 text-sm"
                  min={5}
                  max={300}
                />
                <span className="text-xs text-muted-foreground">seconds</span>
              </div>

              <div className="flex items-center gap-2">
                <Button
                  onClick={startMcpProcess}
                  disabled={!command.trim() || isStarting}
                >
                  {isStarting ? (
                    <Loader2 className="h-4 w-4 animate-spin mr-2" />
                  ) : (
                    <Play className="h-4 w-4 mr-2" />
                  )}
                  Start MCP Process
                </Button>
              </div>

              {startError && (
                <div className="flex items-center gap-2 text-destructive text-sm">
                  <XCircle className="h-4 w-4" />
                  <span>{startError}</span>
                </div>
              )}
            </div>
          </div>
        </TabsContent>

        {/* JSON-RPC Proxy Tab */}
        <TabsContent value="proxy" className="flex-1 mt-2 flex flex-col">
          <div className="border border-border rounded-lg p-3 sm:p-4 bg-muted/30 mb-4">
            <Textarea
              value={proxyBody}
              onChange={(e) => setProxyBody(e.target.value)}
              placeholder='{"jsonrpc": "2.0", "method": "tools/list", "id": 1}'
              className="w-full font-mono bg-background border-2 border-muted-foreground/30 min-h-[80px] resize-y mb-3"
              rows={4}
            />
            <div className="flex items-center justify-between">
              <div className="flex gap-2">
                <Button
                  variant="outline"
                  size="sm"
                  className="text-xs"
                  onClick={() => setProxyBody('{\n  "jsonrpc": "2.0",\n  "method": "tools/list",\n  "id": 1\n}')}
                >
                  tools/list
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  className="text-xs"
                  onClick={() => setProxyBody('{\n  "jsonrpc": "2.0",\n  "method": "resources/list",\n  "id": 1\n}')}
                >
                  resources/list
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  className="text-xs"
                  onClick={() => setProxyBody('{\n  "jsonrpc": "2.0",\n  "method": "prompts/list",\n  "id": 1\n}')}
                >
                  prompts/list
                </Button>
              </div>
              <Button onClick={sendProxyRequest} disabled={!proxyBody.trim() || isSending}>
                {isSending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
                <span className="ml-2">Send</span>
              </Button>
            </div>
          </div>

          {/* Proxy History */}
          <div className="flex-1 overflow-y-auto space-y-3">
            {proxyExecutions.length === 0 ? (
              <div className="text-center text-muted-foreground py-8">
                {isReady
                  ? 'No requests sent yet. Send a JSON-RPC request above.'
                  : 'Start the MCP process first, then send JSON-RPC requests.'}
              </div>
            ) : (
              proxyExecutions.map(exec => (
                <div key={exec.id} className="border border-border rounded-lg bg-card overflow-hidden">
                  <div
                    className="flex items-center gap-2 px-3 py-2 cursor-pointer hover:bg-muted/50"
                    onClick={() => toggleProxyExpanded(exec.id)}
                  >
                    {exec.isExpanded ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
                    <span className="text-sm text-muted-foreground">
                      {exec.startedAt.toLocaleTimeString()}
                    </span>
                    <span className="text-xs bg-purple-500/20 text-purple-600 dark:text-purple-400 px-1.5 py-0.5 rounded">
                      JSON-RPC
                    </span>
                    <code className="text-xs truncate flex-1">
                      {(() => {
                        try { return JSON.parse(exec.requestBody).method || '...' }
                        catch { return '...' }
                      })()}
                    </code>
                    <span className={cn(
                      "text-xs px-1.5 py-0.5 rounded",
                      exec.status === 'sending' && "bg-yellow-500/20 text-yellow-600",
                      exec.status === 'completed' && "bg-green-500/20 text-green-600",
                      exec.status === 'error' && "bg-red-500/20 text-red-600",
                    )}>
                      {exec.status === 'sending' && <Loader2 className="h-3 w-3 animate-spin inline" />}
                      {exec.status === 'completed' && <CheckCircle className="h-3 w-3 inline" />}
                      {exec.status === 'error' && <XCircle className="h-3 w-3 inline" />}
                    </span>
                  </div>
                  {exec.isExpanded && (
                    <div className="border-t border-border">
                      <div className="p-3 bg-muted/20">
                        <div className="text-xs text-muted-foreground mb-1">Request:</div>
                        <pre className="text-xs font-mono whitespace-pre-wrap break-all bg-muted rounded p-2 max-h-[150px] overflow-auto">
                          {exec.requestBody}
                        </pre>
                      </div>
                      {exec.responseBody !== null && (
                        <div className="p-3 bg-muted/10 border-t border-border">
                          <div className="text-xs text-muted-foreground mb-1">Response:</div>
                          <pre className={cn(
                            "text-xs font-mono whitespace-pre-wrap break-all rounded p-2 max-h-[300px] overflow-auto",
                            exec.status === 'error' ? "bg-red-500/10" : "bg-muted"
                          )}>
                            {exec.responseBody}
                          </pre>
                        </div>
                      )}
                    </div>
                  )}
                </div>
              ))
            )}
          </div>
        </TabsContent>

        {/* Info Tab */}
        <TabsContent value="info" className="mt-2">
          <div className="border border-border rounded-lg bg-card overflow-hidden">
            {creationInfo.containerInfo && (
              <div className="px-4 py-3 bg-muted/30 border-b border-border grid grid-cols-2 md:grid-cols-4 gap-3 text-sm">
                <div>
                  <span className="text-muted-foreground block text-xs">Created At</span>
                  <span>{creationInfo.createdAt.toLocaleString()}</span>
                </div>
                <div>
                  <span className="text-muted-foreground block text-xs">Node</span>
                  <span>{creationInfo.containerInfo.nodeName || 'N/A'}</span>
                </div>
                <div>
                  <span className="text-muted-foreground block text-xs">Pod IP</span>
                  <span className="font-mono">{creationInfo.containerInfo.podIP}</span>
                </div>
                <div>
                  <span className="text-muted-foreground block text-xs">Phase</span>
                  <span className="text-green-600 dark:text-green-400">{creationInfo.containerInfo.phase}</span>
                </div>
              </div>
            )}

            {/* Creation Events */}
            {creationInfo.events.length > 0 && (
              <div className="p-4 bg-muted/20 max-h-[200px] overflow-auto">
                <div className="text-xs text-muted-foreground mb-2">Creation Events:</div>
                <ul className="space-y-1 text-sm">
                  {creationInfo.events.map((event, idx) => (
                    <li key={idx} className="flex items-start gap-2">
                      <span className="text-muted-foreground">[{event.eventType}]</span>
                      <span>
                        {event.eventType === 'created' && `Pod ${event.podName} created (${event.phase})`}
                        {event.eventType === 'waiting' && event.message}
                        {event.eventType === 'ready' && `Ready in ${event.elapsedSeconds.toFixed(1)}s`}
                        {event.eventType === 'failed' && event.reason}
                        {event.eventType === 'mcp_starting' && event.message}
                        {event.eventType === 'mcp_started' && `MCP started in ${event.elapsedSeconds.toFixed(1)}s`}
                        {event.eventType === 'mcp_start_failed' && event.reason}
                      </span>
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </div>
        </TabsContent>
      </Tabs>
    </div>
  )
}
