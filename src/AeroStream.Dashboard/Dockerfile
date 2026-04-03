# STAGE 1: Build the React application
FROM node:20-alpine AS build
WORKDIR /app

# Install dependencies first (caches this layer so builds are faster)
COPY package*.json ./
RUN npm install

# Copy the rest of the code and compile the Vite app
COPY . .
RUN npm run build

# STAGE 2: Serve the compiled static files with Nginx
FROM nginx:alpine
# Copy the compiled files from Stage 1 into the Nginx web directory
COPY --from=build /app/dist /usr/share/nginx/html

# Expose port 80 inside the container
EXPOSE 80

# Start Nginx
CMD ["nginx", "-g", "daemon off;"]