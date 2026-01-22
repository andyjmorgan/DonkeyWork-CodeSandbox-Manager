import { useState, useCallback, useRef, useEffect } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Play, Loader2, Terminal, Trash2, ChevronDown, ChevronRight, Search } from 'lucide-react'
import type { ExecutionEvent } from '@/types/api'
import type { CreationInfo } from './SandboxCreator'
import { cn } from '@/lib/utils'

interface CommandExecutorProps {
  sandboxId: string
  creationInfo: CreationInfo
  onDelete?: () => void
}

type ExecutionStatus = 'running' | 'completed' | 'error'

interface CommandExecution {
  id: string
  command: string
  startedAt: Date
  status: ExecutionStatus
  stdout: string
  stderr: string
  exitCode: number | null
  timedOut: boolean
  rawMessages: string[]
  isExpanded: boolean
  type: 'command' | 'query' | 'creation'
  timeout?: number
  creationInfo?: CreationInfo
}

export function CommandExecutor({ sandboxId, creationInfo, onDelete }: CommandExecutorProps) {
  const [command, setCommand] = useState('')
  const [timeout, setTimeout] = useState(300)
  const [executions, setExecutions] = useState<CommandExecution[]>(() => [{
    id: 'creation',
    command: 'Sandbox Created',
    startedAt: creationInfo.createdAt,
    status: 'completed',
    stdout: '',
    stderr: '',
    exitCode: 0,
    timedOut: false,
    rawMessages: creationInfo.rawMessages,
    isExpanded: true,
    type: 'creation',
    creationInfo,
  }])
  const [isExecuting, setIsExecuting] = useState(false)
  const [isQuerying, setIsQuerying] = useState(false)

  const outputRef = useRef<HTMLDivElement>(null)

  // Auto-scroll to top when new execution is added
  useEffect(() => {
    if (outputRef.current) {
      outputRef.current.scrollTop = 0
    }
  }, [executions.length])

  const queryContainerInfo = useCallback(async () => {
    if (isQuerying) return

    const executionId = `query-${Date.now()}`
    const newExecution: CommandExecution = {
      id: executionId,
      command: `GET /api/kata/${sandboxId}`,
      startedAt: new Date(),
      status: 'running',
      stdout: '',
      stderr: '',
      exitCode: null,
      timedOut: false,
      rawMessages: [],
      isExpanded: true,
      type: 'query',
    }

    setExecutions(prev => [newExecution, ...prev])
    setIsQuerying(true)

    try {
      const response = await fetch(`/api/kata/${sandboxId}`)

      const data = await response.json()
      const formattedJson = JSON.stringify(data, null, 2)

      setExecutions(prev => prev.map(exec =>
        exec.id === executionId
          ? {
              ...exec,
              status: response.ok ? 'completed' : 'error',
              stdout: response.ok ? formattedJson : '',
              stderr: !response.ok ? formattedJson : '',
              exitCode: response.ok ? 0 : response.status,
              rawMessages: [formattedJson],
            }
          : exec
      ))
    } catch (error) {
      setExecutions(prev => prev.map(exec =>
        exec.id === executionId
          ? {
              ...exec,
              status: 'error',
              stderr: error instanceof Error ? error.message : 'Failed to query container',
              exitCode: -1,
            }
          : exec
      ))
    } finally {
      setIsQuerying(false)
    }
  }, [sandboxId, isQuerying])

  const executeCommand = useCallback(async () => {
    if (!command.trim() || isExecuting) return

    const executionId = `exec-${Date.now()}`
    const newExecution: CommandExecution = {
      id: executionId,
      command: command.trim(),
      startedAt: new Date(),
      status: 'running',
      stdout: '',
      stderr: '',
      exitCode: null,
      timedOut: false,
      rawMessages: [],
      isExpanded: true,
      type: 'command',
      timeout,
    }

    setExecutions(prev => [newExecution, ...prev])
    setIsExecuting(true)
    const currentCommand = command
    setCommand('')

    try {
      const response = await fetch(`/api/kata/${sandboxId}/execute`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'text/event-stream',
        },
        body: JSON.stringify({
          command: currentCommand,
          timeoutSeconds: timeout,
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

            setExecutions(prev => prev.map(exec =>
              exec.id === executionId
                ? { ...exec, rawMessages: [...exec.rawMessages, jsonStr] }
                : exec
            ))

            try {
              const event = JSON.parse(jsonStr) as ExecutionEvent

              if (event.$type === 'OutputEvent') {
                if (event.stream === 'Stdout') {
                  setExecutions(prev => prev.map(exec =>
                    exec.id === executionId
                      ? { ...exec, stdout: exec.stdout + (exec.stdout ? '\n' : '') + event.data }
                      : exec
                  ))
                } else if (event.stream === 'Stderr') {
                  setExecutions(prev => prev.map(exec =>
                    exec.id === executionId
                      ? { ...exec, stderr: exec.stderr + (exec.stderr ? '\n' : '') + event.data }
                      : exec
                  ))
                }
              } else if (event.$type === 'CompletedEvent') {
                setExecutions(prev => prev.map(exec =>
                  exec.id === executionId
                    ? { ...exec, status: 'completed', exitCode: event.exitCode, timedOut: event.timedOut }
                    : exec
                ))
              }
            } catch (parseError) {
              console.error('Failed to parse event:', parseError)
            }
          }
        }
      }
    } catch (error) {
      setExecutions(prev => prev.map(exec =>
        exec.id === executionId
          ? {
              ...exec,
              status: 'error',
              stderr: exec.stderr + (exec.stderr ? '\n' : '') + (error instanceof Error ? error.message : 'Failed to execute command')
            }
          : exec
      ))
    } finally {
      setIsExecuting(false)
    }
  }, [command, sandboxId, isExecuting, timeout])

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && !isExecuting) {
      e.preventDefault()
      executeCommand()
    }
  }

  const toggleExpanded = (executionId: string) => {
    setExecutions(prev => prev.map(exec =>
      exec.id === executionId
        ? { ...exec, isExpanded: !exec.isExpanded }
        : exec
    ))
  }

  const clearExecutions = () => {
    setExecutions([])
  }

  const deleteSandbox = async () => {
    if (!confirm(`Are you sure you want to delete sandbox ${sandboxId}?`)) {
      return
    }

    try {
      const response = await fetch(`/api/kata/${sandboxId}`, {
        method: 'DELETE',
      })

      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`)
      }

      onDelete?.()
    } catch (error) {
      alert(error instanceof Error ? error.message : 'Failed to delete sandbox')
    }
  }

  const formatTime = (date: Date) => {
    return date.toLocaleTimeString('en-US', { hour12: false })
  }

  const formatDateTime = (date: Date) => {
    return date.toLocaleString('en-US', {
      hour12: false,
      year: 'numeric',
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit'
    })
  }

  const truncateCommand = (cmd: string, maxLen: number = 16) => {
    const firstLine = cmd.split('\n')[0]
    return firstLine.length > maxLen ? firstLine.substring(0, maxLen) + '...' : firstLine
  }

  return (
    <div className="flex flex-col h-full">
      {/* Header with sandbox info and delete button */}
      <div className="flex items-center justify-between mb-4 gap-2">
        <div className="flex items-center gap-2 min-w-0">
          <Terminal className="h-5 w-5 flex-shrink-0" />
          <span className="font-medium hidden sm:inline">Sandbox:</span>
          <code className="text-sm bg-muted px-2 py-1 rounded truncate">{sandboxId}</code>
        </div>
        <div className="flex items-center gap-2 flex-shrink-0">
          <Button variant="outline" size="sm" onClick={queryContainerInfo} disabled={isQuerying} title="Query Info">
            {isQuerying ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
            <span className="hidden sm:inline ml-1">Query Info</span>
          </Button>
          {executions.length > 0 && (
            <Button variant="outline" size="sm" onClick={clearExecutions} title="Clear History">
              <Trash2 className="h-4 w-4" />
              <span className="hidden sm:inline ml-1">Clear History</span>
            </Button>
          )}
          <Button variant="destructive" size="sm" onClick={deleteSandbox} title="Delete Sandbox">
            <Trash2 className="h-4 w-4" />
            <span className="hidden sm:inline ml-1">Delete</span>
          </Button>
        </div>
      </div>

      {/* Command Input - Fixed at top */}
      <div className="border border-border rounded-lg p-3 sm:p-4 bg-muted/30 mb-4">
        <Textarea
          value={command}
          onChange={(e) => setCommand(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder="Enter command (e.g., ls -la)&#10;Ctrl+Enter to execute"
          disabled={isExecuting}
          className="w-full font-mono bg-background border-2 border-muted-foreground/30 min-h-[60px] sm:min-h-[80px] resize-y mb-3"
          rows={2}
        />
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
          <div className="flex items-center gap-2">
            <label className="text-xs text-muted-foreground whitespace-nowrap">Timeout:</label>
            <Input
              type="number"
              value={timeout}
              onChange={(e) => setTimeout(Math.max(1, Math.min(600, parseInt(e.target.value) || 300)))}
              className="w-16 h-8 text-sm"
              min={1}
              max={600}
            />
            <span className="text-xs text-muted-foreground">sec</span>
          </div>
          <Button
            onClick={executeCommand}
            disabled={!command.trim() || isExecuting}
            className="w-full sm:w-auto"
          >
            {isExecuting ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <Play className="h-4 w-4" />
            )}
            <span className="ml-2">Execute</span>
          </Button>
        </div>
      </div>

      {/* Execution History - Full Width */}
      <div ref={outputRef} className="flex-1 overflow-y-auto space-y-3">
        {executions.length === 0 ? (
          <div className="text-center text-muted-foreground py-8">
            No commands executed yet. Enter a command above to get started.
          </div>
        ) : (
          executions.map(exec => (
            <ExecutionCard
              key={exec.id}
              execution={exec}
              onToggle={() => toggleExpanded(exec.id)}
              formatTime={formatTime}
              formatDateTime={formatDateTime}
              truncateCommand={truncateCommand}
            />
          ))
        )}
      </div>
    </div>
  )
}

interface ExecutionCardProps {
  execution: CommandExecution
  onToggle: () => void
  formatTime: (date: Date) => string
  formatDateTime: (date: Date) => string
  truncateCommand: (cmd: string, maxLen?: number) => string
}

function ExecutionCard({ execution, onToggle, formatTime, formatDateTime, truncateCommand }: ExecutionCardProps) {
  const { command, startedAt, status, stdout, stderr, exitCode, timedOut, rawMessages, isExpanded, type, timeout, creationInfo } = execution

  // Special rendering for creation type
  if (type === 'creation' && creationInfo) {
    return (
      <div className="border border-border rounded-lg bg-card overflow-hidden">
        {/* Card Header */}
        <div
          className="flex items-center gap-2 sm:gap-3 px-3 sm:px-4 py-3 cursor-pointer hover:bg-muted/50 transition-colors"
          onClick={onToggle}
        >
          <button className="flex-shrink-0">
            {isExpanded ? (
              <ChevronDown className="h-4 w-4" />
            ) : (
              <ChevronRight className="h-4 w-4" />
            )}
          </button>

          <span className="text-sm text-muted-foreground flex-shrink-0 hidden sm:inline">
            {formatTime(startedAt)}
          </span>

          <span className="text-xs bg-purple-500/20 text-purple-600 dark:text-purple-400 px-1.5 py-0.5 rounded flex-shrink-0">
            CREATED
          </span>

          <span className="text-sm truncate flex-1 min-w-0 hidden sm:inline">
            Sandbox Created
          </span>

          <span className="text-xs text-muted-foreground flex-shrink-0">
            {creationInfo.elapsedSeconds.toFixed(1)}s
          </span>

          <span className="text-xs px-1.5 sm:px-2 py-1 rounded bg-green-500/20 text-green-600 dark:text-green-400 flex-shrink-0">
            Ready
          </span>
        </div>

        {/* Expanded Content */}
        {isExpanded && (
          <div className="border-t border-border">
            {/* Container Info */}
            {creationInfo.containerInfo && (
              <div className="px-4 py-3 bg-muted/30 border-b border-border grid grid-cols-2 md:grid-cols-4 gap-3 text-sm">
                <div>
                  <span className="text-muted-foreground block text-xs">Created At</span>
                  <span>{formatDateTime(creationInfo.createdAt)}</span>
                </div>
                <div>
                  <span className="text-muted-foreground block text-xs">Node</span>
                  <span>{creationInfo.containerInfo.nodeName}</span>
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

            {/* Events/Debug Tabs */}
            <Tabs defaultValue="events" className="w-full">
              <TabsList className="w-full justify-start rounded-none border-b border-border bg-transparent h-auto p-0">
                <TabsTrigger
                  value="events"
                  className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary px-4 py-2"
                >
                  Events
                </TabsTrigger>
                <TabsTrigger
                  value="debug"
                  className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary px-4 py-2"
                >
                  Debug
                </TabsTrigger>
              </TabsList>

              <TabsContent value="events" className="m-0">
                <div className="p-4 bg-muted/20 max-h-[200px] overflow-auto">
                  <ul className="space-y-1 text-sm">
                    {creationInfo.events.map((event, idx) => (
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
                </div>
              </TabsContent>

              <TabsContent value="debug" className="m-0">
                <div className="p-4 bg-muted/20 max-h-[200px] overflow-auto terminal-output">
                  <pre className="text-xs whitespace-pre-wrap break-all">
                    {rawMessages.map((msg, idx) => (
                      <div key={idx} className="mb-1">
                        <span className="text-muted-foreground">{idx + 1}:</span> {msg}
                      </div>
                    ))}
                  </pre>
                </div>
              </TabsContent>
            </Tabs>
          </div>
        )}
      </div>
    )
  }

  // Regular command/query rendering
  return (
    <div className="border border-border rounded-lg bg-card overflow-hidden">
      {/* Card Header - Clickable to expand/collapse */}
      <div
        className="flex items-center gap-2 sm:gap-3 px-3 sm:px-4 py-3 cursor-pointer hover:bg-muted/50 transition-colors"
        onClick={onToggle}
      >
        <button className="flex-shrink-0">
          {isExpanded ? (
            <ChevronDown className="h-4 w-4" />
          ) : (
            <ChevronRight className="h-4 w-4" />
          )}
        </button>

        <span className="text-sm text-muted-foreground flex-shrink-0 hidden sm:inline">
          {formatTime(startedAt)}
        </span>

        {type === 'query' ? (
          <span className="text-xs bg-blue-500/20 text-blue-600 dark:text-blue-400 px-1.5 py-0.5 rounded flex-shrink-0">
            QUERY
          </span>
        ) : (
          <span className="text-xs bg-orange-500/20 text-orange-600 dark:text-orange-400 px-1.5 py-0.5 rounded flex-shrink-0">
            CMD
          </span>
        )}

        <code className="text-sm font-mono truncate flex-1 min-w-0" title={command}>
          {truncateCommand(command, 16)}
        </code>

        {timeout && type === 'command' && (
          <span className="text-xs text-muted-foreground flex-shrink-0 hidden sm:inline">
            {timeout}s
          </span>
        )}

        <span className={cn(
          "text-xs px-1.5 sm:px-2 py-1 rounded flex-shrink-0",
          status === 'running' && "bg-yellow-500/20 text-yellow-600 dark:text-yellow-400",
          status === 'completed' && exitCode === 0 && "bg-green-500/20 text-green-600 dark:text-green-400",
          status === 'completed' && exitCode !== 0 && "bg-red-500/20 text-red-600 dark:text-red-400",
          status === 'error' && "bg-red-500/20 text-red-600 dark:text-red-400"
        )}>
          {status === 'running' && (
            <span className="flex items-center gap-1">
              <Loader2 className="h-3 w-3 animate-spin" />
              <span className="hidden sm:inline">Running</span>
            </span>
          )}
          {status === 'completed' && (
            <span>
              <span className="hidden sm:inline">Exit: </span>{exitCode}{timedOut ? ' (t/o)' : ''}
            </span>
          )}
          {status === 'error' && 'Error'}
        </span>
      </div>

      {/* Expanded Content */}
      {isExpanded && (
        <div className="border-t border-border">
          {/* Full Command */}
          <div className="px-3 sm:px-4 py-2 bg-muted/30 border-b border-border">
            <span className="text-xs text-muted-foreground hidden sm:inline">Command:</span>
            <code className="block font-mono text-sm sm:mt-1 break-all whitespace-pre-wrap">{command}</code>
          </div>

          {/* Output Tabs - Full Width */}
          <Tabs defaultValue="output" className="w-full">
            <TabsList className="w-full justify-start rounded-none border-b border-border bg-transparent h-auto p-0">
              <TabsTrigger
                value="output"
                className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary px-4 py-2"
              >
                Output
              </TabsTrigger>
              <TabsTrigger
                value="debug"
                className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary px-4 py-2"
              >
                Debug
              </TabsTrigger>
            </TabsList>

            <TabsContent value="output" className="m-0">
              <div className="p-4 bg-muted/20 min-h-[100px] max-h-[400px] overflow-auto terminal-output">
                {!stdout && !stderr && status === 'running' ? (
                  <span className="text-muted-foreground">Waiting for output...</span>
                ) : !stdout && !stderr ? (
                  <span className="text-muted-foreground">No output</span>
                ) : (
                  <pre className="whitespace-pre-wrap break-all">
                    {stdout}
                    {stderr && (
                      <span className="text-red-500 dark:text-red-400">{stdout ? '\n' : ''}{stderr}</span>
                    )}
                  </pre>
                )}
              </div>
            </TabsContent>

            <TabsContent value="debug" className="m-0">
              <div className="p-4 bg-muted/20 min-h-[100px] max-h-[400px] overflow-auto terminal-output">
                {rawMessages.length === 0 ? (
                  <span className="text-muted-foreground">No raw messages</span>
                ) : (
                  <pre className="text-xs whitespace-pre-wrap break-all">
                    {rawMessages.map((msg, idx) => (
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
  )
}
