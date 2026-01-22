import type { ReactNode } from 'react'
import { ThemeToggle } from './ThemeToggle'
import { Github } from 'lucide-react'
import { Button } from '@/components/ui/button'

interface AppLayoutProps {
  children: ReactNode
}

export function AppLayout({ children }: AppLayoutProps) {
  return (
    <div className="flex flex-col h-screen bg-background text-foreground">
      {/* Header */}
      <header className="border-b border-border">
        <div className="px-4 py-3 flex items-center justify-between">
          <div className="flex items-center gap-3">
            <img
              src="/donkeywork.png"
              alt="DonkeyWork Logo"
              className="w-7 h-7"
            />
            <h1 className="text-xl font-semibold">Sandbox Manager</h1>
          </div>
          <div className="flex items-center gap-2">
            <Button
              variant="ghost"
              size="icon"
              asChild
            >
              <a
                href="https://github.com/andyjmorgan/DonkeyWork-CodeSandbox-Manager"
                target="_blank"
                rel="noopener noreferrer"
                title="View on GitHub"
              >
                <Github className="h-5 w-5" />
              </a>
            </Button>
            <ThemeToggle />
          </div>
        </div>
      </header>

      {/* Main content area */}
      <main className="flex-1 overflow-hidden">
        {children}
      </main>
    </div>
  )
}
