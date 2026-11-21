package com.carriez.flutter_hbb

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.content.FileProvider
import androidx.core.content.pm.PackageInfoCompat
import io.flutter.plugin.common.MethodChannel
import java.io.File

class PhoneWorkspaceUpdateProvider : FileProvider()

internal fun Activity.installPhoneWorkspaceUpdate(path: String?, result: MethodChannel.Result) {
    try {
        require(packageName == "com.carriez.flutter_hbb.phoneworkspace" && path != null)
        val root = File(cacheDir, "phone-workspace-updates").canonicalFile
        val apk = File(path).canonicalFile
        require(apk.parentFile?.parentFile == root && apk.name == "phone-workspace-arm64.apk" && apk.isFile)
        val archive = packageManager.getPackageArchiveInfo(apk.path, 0)
        require(archive != null && archive.packageName == packageName &&
            PackageInfoCompat.getLongVersionCode(archive) >
                PackageInfoCompat.getLongVersionCode(packageManager.getPackageInfo(packageName, 0))) {
            "The APK is not a newer version of this application"
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !packageManager.canRequestPackageInstalls()) {
            startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")))
            result.success(false)
            return
        }
        val uri = FileProvider.getUriForFile(this, "$packageName.updates", apk)
        startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION))
        result.success(true)
    } catch (e: Exception) {
        result.error("update_install", e.localizedMessage, null)
    }
}
