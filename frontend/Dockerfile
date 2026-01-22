# Build stage
FROM node:22-alpine AS builder

WORKDIR /app

# Copy package files
COPY package*.json ./

# Install dependencies
RUN npm ci

# Copy source code
COPY . .

# Build the application
RUN npm run build

# Production stage
FROM nginx:alpine

# Copy custom nginx config template
COPY nginx.conf /etc/nginx/templates/default.conf.template

# Copy built assets from builder stage
COPY --from=builder /app/dist /usr/share/nginx/html

# Default API backend URL (can be overridden at runtime)
ENV API_BACKEND_URL=http://localhost:8668

# Expose port 80
EXPOSE 80

# nginx:alpine uses envsubst automatically with /etc/nginx/templates/*.template
