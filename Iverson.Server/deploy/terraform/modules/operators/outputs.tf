output "storage_class_names" {
  value = {
    postgres  = kubernetes_storage_class.postgres.metadata[0].name
    starrocks = kubernetes_storage_class.starrocks.metadata[0].name
    qdrant    = kubernetes_storage_class.qdrant.metadata[0].name
    kafka     = kubernetes_storage_class.kafka.metadata[0].name
    ollama    = kubernetes_storage_class.ollama.metadata[0].name
  }
}
