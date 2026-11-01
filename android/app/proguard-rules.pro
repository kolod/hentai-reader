# JNI: QuickJsNative's native methods and the callback interface are looked up by name from C.
-keep class hentaireader.host.engine.QuickJsNative { *; }
-keep class hentaireader.host.engine.QuickJsNative$Callbacks { *; }
-keep class * implements hentaireader.host.engine.QuickJsNative$Callbacks { *; }
# The JS adapter is a classpath resource, loaded by absolute path.
