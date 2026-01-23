import { TrendingUp, Activity } from 'lucide-react'
import type { PoolStatusResponse } from '@/types/api'

interface PoolChartProps {
  poolStatus: PoolStatusResponse
}

export function PoolChart({ poolStatus }: PoolChartProps) {
  const { creating, warm, allocated, targetSize, readyPercentage, utilizationPercentage } = poolStatus

  // Calculate percentages for visual display
  const total = creating + warm + allocated
  const creatingPercent = total > 0 ? (creating / total) * 100 : 0
  const warmPercent = total > 0 ? (warm / total) * 100 : 0
  const allocatedPercent = total > 0 ? (allocated / total) * 100 : 0

  return (
    <div className="space-y-4">
      {/* Stacked Bar Chart */}
      <div>
        <div className="flex items-center justify-between mb-2">
          <span className="text-sm font-medium">Pool Composition</span>
          <span className="text-xs text-muted-foreground">
            {total} total
          </span>
        </div>
        <div className="h-8 w-full bg-muted rounded-lg overflow-hidden flex">
          {creating > 0 && (
            <div
              className="bg-yellow-500 dark:bg-yellow-600 transition-all duration-300 flex items-center justify-center text-xs font-semibold text-white"
              style={{ width: `${creatingPercent}%` }}
              title={`${creating} creating (${creatingPercent.toFixed(1)}%)`}
            >
              {creating > 0 && creatingPercent > 10 && creating}
            </div>
          )}
          {warm > 0 && (
            <div
              className="bg-green-500 dark:bg-green-600 transition-all duration-300 flex items-center justify-center text-xs font-semibold text-white"
              style={{ width: `${warmPercent}%` }}
              title={`${warm} warm (${warmPercent.toFixed(1)}%)`}
            >
              {warm > 0 && warmPercent > 10 && warm}
            </div>
          )}
          {allocated > 0 && (
            <div
              className="bg-blue-500 dark:bg-blue-600 transition-all duration-300 flex items-center justify-center text-xs font-semibold text-white"
              style={{ width: `${allocatedPercent}%` }}
              title={`${allocated} allocated (${allocatedPercent.toFixed(1)}%)`}
            >
              {allocated > 0 && allocatedPercent > 10 && allocated}
            </div>
          )}
        </div>
        <div className="flex items-center justify-between mt-2 text-xs">
          <div className="flex gap-3">
            {creating > 0 && (
              <div className="flex items-center gap-1">
                <div className="w-3 h-3 rounded bg-yellow-500 dark:bg-yellow-600"></div>
                <span className="text-muted-foreground">Creating ({creating})</span>
              </div>
            )}
            <div className="flex items-center gap-1">
              <div className="w-3 h-3 rounded bg-green-500 dark:bg-green-600"></div>
              <span className="text-muted-foreground">Warm ({warm})</span>
            </div>
            <div className="flex items-center gap-1">
              <div className="w-3 h-3 rounded bg-blue-500 dark:bg-blue-600"></div>
              <span className="text-muted-foreground">In Use ({allocated})</span>
            </div>
          </div>
        </div>
      </div>

      {/* Stats Grid */}
      <div className="grid grid-cols-2 gap-3">
        <div className="border border-border rounded-lg p-3 bg-gradient-to-br from-green-50 to-green-100 dark:from-green-950 dark:to-green-900">
          <div className="flex items-center gap-2 mb-1">
            <Activity className="h-4 w-4 text-green-600 dark:text-green-400" />
            <span className="text-xs text-muted-foreground">Ready</span>
          </div>
          <div className="text-2xl font-bold text-green-700 dark:text-green-300">
            {readyPercentage.toFixed(0)}%
          </div>
          <div className="text-xs text-muted-foreground mt-1">
            {warm} of {targetSize} target
          </div>
        </div>

        <div className="border border-border rounded-lg p-3 bg-gradient-to-br from-blue-50 to-blue-100 dark:from-blue-950 dark:to-blue-900">
          <div className="flex items-center gap-2 mb-1">
            <TrendingUp className="h-4 w-4 text-blue-600 dark:text-blue-400" />
            <span className="text-xs text-muted-foreground">Utilization</span>
          </div>
          <div className="text-2xl font-bold text-blue-700 dark:text-blue-300">
            {utilizationPercentage.toFixed(0)}%
          </div>
          <div className="text-xs text-muted-foreground mt-1">
            {allocated} of {total} active
          </div>
        </div>
      </div>

      {/* Health Indicator */}
      <div className="flex items-center gap-2 p-2 rounded-lg bg-muted/50">
        <div className={`w-2 h-2 rounded-full animate-pulse ${
          warm >= targetSize * 0.8 ? 'bg-green-500' :
          warm >= targetSize * 0.5 ? 'bg-yellow-500' :
          'bg-red-500'
        }`}></div>
        <span className="text-xs text-muted-foreground">
          {warm >= targetSize * 0.8 ? 'Pool healthy' :
           warm >= targetSize * 0.5 ? 'Pool below target' :
           'Pool critically low'}
        </span>
      </div>
    </div>
  )
}
