// Container creation events
export interface ContainerCreatedEvent {
  eventType: 'created';
  podName: string;
  phase: string;
}

export interface ContainerWaitingEvent {
  eventType: 'waiting';
  podName: string;
  attemptNumber: number;
  phase: string;
  message: string;
}

export interface ContainerReadyEvent {
  eventType: 'ready';
  podName: string;
  containerInfo: KataContainerInfo;
  elapsedSeconds: number;
}

export interface ContainerFailedEvent {
  eventType: 'failed';
  podName: string;
  reason: string;
  containerInfo?: KataContainerInfo;
}

export type ContainerEvent =
  | ContainerCreatedEvent
  | ContainerWaitingEvent
  | ContainerReadyEvent
  | ContainerFailedEvent;

// Container info
export interface KataContainerInfo {
  name: string;
  phase: string;
  isReady: boolean;
  createdAt: string;
  nodeName: string;
  podIP: string;
  labels: Record<string, string>;
  image: string;
}

// Execution events
export interface OutputEvent {
  $type: 'OutputEvent';
  pid: number;
  stream: 'Stdout' | 'Stderr';
  data: string;
}

export interface CompletedEvent {
  $type: 'CompletedEvent';
  pid: number;
  exitCode: number;
  timedOut: boolean;
}

export type ExecutionEvent = OutputEvent | CompletedEvent;

// Request types
export interface CreateContainerRequest {
  labels?: Record<string, string>;
  environmentVariables?: Record<string, string>;
  resources?: {
    requests?: { memoryMi?: number; cpuMillicores?: number };
    limits?: { memoryMi?: number; cpuMillicores?: number };
  };
  waitForReady?: boolean;
}

export interface ExecuteCommandRequest {
  command: string;
  timeoutSeconds?: number;
}

// Delete response
export interface DeleteContainerResponse {
  success: boolean;
  message: string;
  podName: string;
}

export interface DeleteAllContainersResponse {
  deletedCount: number;
  failedCount: number;
  deletedPods: string[];
  failedPods: string[];
}

// Allocation types
export interface AllocateSandboxRequest {
  userId: string;
}

export interface PoolStatusResponse {
  creating: number;
  warm: number;
  allocated: number;
  manual: number;
  total: number;
  targetSize: number;
  maxTotalContainers: number;
  readyPercentage: number;
  utilizationPercentage: number;
}

// MCP Server types
export interface McpServerInfo {
  name: string;
  phase: string;
  isReady: boolean;
  createdAt: string;
  lastActivity: string | null;
  nodeName: string | null;
  podIP: string | null;
  labels: Record<string, string> | null;
  image: string | null;
  launchCommand: string | null;
  mcpStatus: McpProcessStatus;
}

export type McpProcessStatus = 'Unknown' | 'Idle' | 'Initializing' | 'Ready' | 'Error' | 'Disposed';

export interface CreateMcpServerRequest {
  launchCommand?: string;
  preExecScripts?: string[];
  timeoutSeconds?: number;
  resources?: {
    requests?: { memoryMi?: number; cpuMillicores?: number };
    limits?: { memoryMi?: number; cpuMillicores?: number };
  };
  labels?: Record<string, string>;
  environmentVariables?: Record<string, string>;
}

export interface McpStartRequest {
  launchCommand: string;
  preExecScripts?: string[];
  timeoutSeconds?: number;
}

export interface McpStatusResponse {
  state: string;
  error: string | null;
  startedAt: string | null;
  lastRequestAt: string | null;
}

// MCP-specific creation events
export interface McpServerStartingEvent {
  eventType: 'mcp_starting';
  podName: string;
  message: string;
}

export interface McpServerStartedEvent {
  eventType: 'mcp_started';
  podName: string;
  serverInfo: McpServerInfo;
  elapsedSeconds: number;
}

export interface McpServerStartFailedEvent {
  eventType: 'mcp_start_failed';
  podName: string;
  reason: string;
}

export type McpCreationEvent =
  | ContainerCreatedEvent
  | ContainerWaitingEvent
  | ContainerReadyEvent
  | ContainerFailedEvent
  | McpServerStartingEvent
  | McpServerStartedEvent
  | McpServerStartFailedEvent;
