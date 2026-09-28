FROM golang:1.25 AS build
WORKDIR /src
COPY go.mod go.sum* ./
RUN go mod download
COPY . .
ARG VERSION=dev
RUN CGO_ENABLED=0 go build -trimpath -ldflags "-s -w -X main.version=${VERSION}" -o /icc-agent ./cmd/icc-agent

FROM gcr.io/distroless/static-debian12
COPY --from=build /icc-agent /icc-agent
ENTRYPOINT ["/icc-agent"]
