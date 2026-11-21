import 'dart:convert';

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/physics.dart';
import 'package:flutter/scheduler.dart';
import 'package:flutter_hbb/models/input_model.dart';
import 'package:flutter_hbb/models/model.dart';
import 'package:flutter_hbb/models/platform_model.dart';

class PhoneWorkspaceGestures extends StatefulWidget {
  final FFI ffi;
  final Widget child;
  const PhoneWorkspaceGestures(
      {super.key, required this.ffi, required this.child});

  @override
  State<PhoneWorkspaceGestures> createState() => _PhoneWorkspaceGesturesState();
}

class _PhoneWorkspaceGesturesState extends State<PhoneWorkspaceGestures>
    with SingleTickerProviderStateMixin {
  late final Ticker _ticker = createTicker(_coast);
  late FrictionSimulation _horizontal, _vertical;
  Offset _last = Offset.zero;
  Offset _remainder = Offset.zero;
  bool _stopTap = false;
  bool _scrolling = false;
  bool _pinching = false;
  double _scale = 1;
  Offset? _hold;
  bool _selecting = false;
  Future<void> _selection = Future.value();

  bool get _enabled =>
      mounted &&
      widget.ffi.ffiModel.pi.isPhoneWorkspace &&
      widget.ffi.ffiModel.keyboard &&
      !widget.ffi.ffiModel.viewOnly;
  bool _inside(Offset point) =>
      _enabled &&
      !widget.ffi.cursorModel.shouldBlock(point.dx, point.dy) &&
      widget.ffi.cursorModel.isInRemoteRect(point);

  Future<void> _tap(Offset point, MouseButtons button) async {
    if (_inside(point) &&
        await widget.ffi.cursorModel.move(point.dx, point.dy) &&
        _enabled) await widget.ffi.inputModel.tap(button);
  }

  void _scroll(Offset delta) {
    if (!_enabled) return;
    _remainder += delta;
    final x = _remainder.dx.truncate(), y = _remainder.dy.truncate();
    _remainder -= Offset(x.toDouble(), y.toDouble());
    if (x == 0 && y == 0) return;
    bind.sessionSendMouse(
        sessionId: widget.ffi.sessionId,
        msg: jsonEncode({
          'type': 'trackpad',
          'x': x.toString(),
          'y': y.toString(),
        }));
  }

  void _coast(Duration elapsed) {
    final t = elapsed.inMicroseconds / Duration.microsecondsPerSecond;
    if (!_enabled ||
        (_horizontal.dx(t).abs() < 10 && _vertical.dx(t).abs() < 10)) {
      _ticker.stop();
      return;
    }
    final next = Offset(_horizontal.x(t), _vertical.x(t));
    _scroll(next - _last);
    _last = next;
  }

  void _dragSelection(LongPressMoveUpdateDetails event) {
    final origin = _hold;
    if (origin == null || !_inside(event.localPosition)) return;
    final first = !_selecting;
    if (first && (event.localPosition - origin).distance < kTouchSlop) return;
    _selecting = true;
    final ffi = widget.ffi;
    _selection = _selection.then((_) async {
      if (!_enabled) return;
      if (first) {
        if (!await ffi.cursorModel.move(origin.dx, origin.dy) || !_enabled)
          return;
        await ffi.inputModel.tapDown(MouseButtons.left);
      }
      if (_enabled)
        await ffi.cursorModel
            .move(event.localPosition.dx, event.localPosition.dy);
    });
  }

  void _endSelection() {
    if (_selecting) {
      final input = widget.ffi.inputModel;
      _selection =
          _selection.whenComplete(() => input.tapUp(MouseButtons.left));
    }
    _selecting = false;
    _hold = null;
  }

  @override
  void dispose() {
    _endSelection();
    _ticker.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Listener(
        onPointerDown: (_) {
          _stopTap = _ticker.isActive;
          _ticker.stop();
        },
        onPointerCancel: (_) => _endSelection(),
        child: GestureDetector(
          supportedDevices: const {
            PointerDeviceKind.touch,
            PointerDeviceKind.stylus
          },
          onTapUp: (d) {
            if (!_stopTap) _tap(d.localPosition, MouseButtons.left);
          },
          onLongPressStart: (d) {
            _hold = _inside(d.localPosition) ? d.localPosition : null;
          },
          onLongPressMoveUpdate: _dragSelection,
          onLongPressEnd: (_) {
            final point = _hold;
            if (!_selecting && point != null) _tap(point, MouseButtons.right);
            _endSelection();
          },
          onScaleStart: (d) {
            _scrolling = _inside(d.localFocalPoint);
            _pinching = d.pointerCount > 1;
            _scale = 1;
            _remainder = Offset.zero;
            if (_scrolling)
              widget.ffi.cursorModel
                  .move(d.localFocalPoint.dx, d.localFocalPoint.dy);
          },
          onScaleUpdate: (d) {
            if (!_scrolling || !_enabled) return;
            if (d.pointerCount > 1) {
              _pinching = true;
              final canvas = widget.ffi.canvasModel;
              if (!canvas.locked) {
                canvas.updateScale(d.scale / _scale, d.focalPoint);
                canvas.panX(d.focalPointDelta.dx);
                canvas.panY(d.focalPointDelta.dy);
              }
              _scale = d.scale;
            } else if (!_pinching) {
              _scroll(d.focalPointDelta);
            }
          },
          onScaleEnd: (d) {
            final velocity = d.velocity.pixelsPerSecond;
            if (_scrolling &&
                !_pinching &&
                velocity.distance > kMinFlingVelocity) {
              _horizontal = FrictionSimulation(
                  0.01, 0, velocity.dx.clamp(-4000, 4000).toDouble());
              _vertical = FrictionSimulation(
                  0.01, 0, velocity.dy.clamp(-4000, 4000).toDouble());
              _last = Offset.zero;
              _ticker.start();
            }
            _scrolling = false;
          },
          child: widget.child,
        ),
      );
}
