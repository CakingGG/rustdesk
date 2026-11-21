import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math' as math;
import 'dart:typed_data';

import 'package:crypto/crypto.dart';

const _repository = 'CakingGG/rustdesk';
const _partSize = 512 * 1024;
const _parallelDownloads = 4;
const _readTimeout = Duration(seconds: 45);
final _tag = RegExp(r'^phone-workspace-([1-9][0-9]*)$');
final _digest = RegExp(r'^sha256:([0-9a-f]{64})$');

class PhoneWorkspaceRelease {
  final int build;
  final String assetName;
  final int size;
  final String digest;

  PhoneWorkspaceRelease._(this.build, this.assetName, this.size, this.digest);

  static Future<PhoneWorkspaceRelease> check(HttpClient client,
      {required bool android}) async {
    Map<String, dynamic>? latest;
    var build = 0;
    for (var page = 1;; page++) {
      final uri = Uri.https('api.github.com', '/repos/$_repository/releases',
          {'per_page': '100', 'page': '$page'});
      final request = await client.getUrl(uri);
      request.headers
          .set(HttpHeaders.acceptHeader, 'application/vnd.github+json');
      final response =
          await request.close().timeout(const Duration(seconds: 30));
      if (response.statusCode != HttpStatus.ok) {
        throw HttpException('GitHub HTTP ${response.statusCode}', uri: uri);
      }
      final releases = jsonDecode(await utf8.decoder
          .bind(response.timeout(const Duration(seconds: 30)))
          .join()) as List;
      for (final release in releases.whereType<Map<String, dynamic>>()) {
        final tag = _tag.firstMatch(release['tag_name'] as String? ?? '');
        if (tag == null || release['draft'] != false) continue;
        final number = int.parse(tag[1]!);
        if (number > build) {
          build = number;
          latest = release;
        }
      }
      if (releases.length < 100) break;
    }
    if (latest == null) {
      throw const FormatException('No published phone workspace build');
    }
    final name =
        android ? 'phone-workspace-arm64.apk' : 'phone-workspace-x64.msi';
    final asset = (latest['assets'] as List)
        .whereType<Map<String, dynamic>>()
        .singleWhere((a) => a['name'] == name && a['state'] == 'uploaded');
    final hash = _digest.firstMatch(asset['digest'] as String? ?? '');
    final bytes = asset['size'];
    if (hash == null || bytes is! int || bytes <= 0) {
      throw const FormatException(
          'Missing installer size or SHA-256 on GitHub');
    }
    return PhoneWorkspaceRelease._(build, name, bytes, hash[1]!);
  }

  Future<(Uri, HttpClientResponse)> _getRange(
      HttpClient client, Uri uri, int start, int end) async {
    for (var redirects = 0;; redirects++) {
      final request = await client.getUrl(uri);
      request.followRedirects = false;
      request.headers.set(HttpHeaders.rangeHeader, 'bytes=$start-$end');
      request.headers.set(HttpHeaders.acceptEncodingHeader, 'identity');
      final response =
          await request.close().timeout(const Duration(seconds: 30));
      if (!response.isRedirect) return (uri, response);
      final location = response.headers.value(HttpHeaders.locationHeader);
      if (redirects >= 4 || location == null) {
        throw HttpException('Invalid installer redirect', uri: uri);
      }
      uri = uri.resolve(location);
      if (uri.scheme != 'https' ||
          uri.port != 443 ||
          uri.userInfo.isNotEmpty ||
          !{
            'github.com',
            'release-assets.githubusercontent.com',
            'objects.githubusercontent.com'
          }.contains(uri.host)) {
        throw HttpException('Unexpected installer host', uri: uri);
      }
      await response.drain<void>().timeout(const Duration(seconds: 30));
    }
  }

  Future<Uint8List> _readPart(HttpClient client, Uri uri, int start,
      HttpClientResponse? response, void Function(int) onBytes) async {
    final end = math.min(start + _partSize, size) - 1;
    final bytes = BytesBuilder(copy: false);
    for (var attempt = 0;; attempt++) {
      final offset = start + bytes.length;
      try {
        response ??= (await _getRange(client, uri, offset, end)).$2;
        if (response.statusCode != HttpStatus.partialContent ||
            response.headers.value(HttpHeaders.contentRangeHeader) !=
                'bytes $offset-$end/$size') {
          await response.listen((_) {}).cancel();
          throw const FormatException('Unexpected installer byte range');
        }
        await for (final chunk in response.timeout(_readTimeout)) {
          if (start + bytes.length + chunk.length > end + 1) {
            throw const FormatException(
                'Installer exceeds its requested range');
          }
          bytes.add(chunk);
          onBytes(chunk.length);
        }
        if (start + bytes.length == end + 1) return bytes.takeBytes();
        throw const HttpException('Incomplete installer byte range');
      } catch (e) {
        if (attempt != 0 ||
            start + bytes.length > end ||
            (e is! IOException && e is! TimeoutException)) rethrow;
        response = null;
      }
    }
  }

  Future<File> download(HttpClient client, Directory cache,
      void Function(double) onProgress) async {
    final url = Uri.https('github.com',
        '/$_repository/releases/download/phone-workspace-$build/$assetName');
    final (uri, response) =
        await _getRange(client, url, 0, math.min(_partSize, size) - 1);
    final partial = response.statusCode == HttpStatus.partialContent;
    if (!partial && response.statusCode != HttpStatus.ok) {
      await response.listen((_) {}).cancel();
      throw HttpException('Download HTTP ${response.statusCode}',
          uri: uri.replace(query: ''));
    }
    final root = await Directory('${cache.path}/phone-workspace-updates')
        .create(recursive: true);
    final directory = await root.createTemp('build-$build-');
    final file = File('${directory.path}/$assetName');
    try {
      final sink = file.openWrite();
      var received = 0;
      void onBytes(int count) {
        received += count;
        if (received > size) {
          throw const FormatException('Installer exceeds its published size');
        }
        onProgress(received / size);
      }

      try {
        if (partial) {
          for (var batch = 0;
              batch < size;
              batch += _parallelDownloads * _partSize) {
            final limit =
                math.min(batch + _parallelDownloads * _partSize, size);
            final parts = await Future.wait([
              for (var start = batch; start < limit; start += _partSize)
                _readPart(
                    client, uri, start, start == 0 ? response : null, onBytes)
            ]);
            await sink.addStream(Stream.fromIterable(parts));
          }
        } else {
          await sink.addStream(response.timeout(_readTimeout).map((chunk) {
            onBytes(chunk.length);
            return chunk;
          }));
        }
      } finally {
        await sink.close();
      }
      if (received != size ||
          (await sha256.bind(file.openRead()).first).toString() != digest) {
        throw const FormatException(
            'Installer does not match the published SHA-256 or size');
      }
      return file;
    } catch (_) {
      await directory.delete(recursive: true);
      rethrow;
    }
  }
}
