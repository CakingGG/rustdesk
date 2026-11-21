import 'dart:convert';
import 'package:flutter/widgets.dart';
import 'package:flutter_hbb/common.dart' show SessionID;
import 'package:flutter_hbb/generated_bridge.dart' show RustdeskImpl;
import 'package:flutter_hbb/mobile/widgets/phone_workspace_gestures.dart';
import 'package:flutter_hbb/models/input_model.dart';
import 'package:flutter_hbb/models/model.dart';
import 'package:flutter_hbb/models/platform_model.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:uuid/uuid.dart';

class _Model extends Fake implements FfiModel {
  @override
  final pi = PeerInfo();
  @override
  bool get keyboard => true;
  @override
  bool get viewOnly => false;
}

class _Cursor extends Fake implements CursorModel {
  @override
  bool shouldBlock(double x, double y) => false;
  @override
  bool isInRemoteRect(Offset point) => true;
  @override
  Future<bool> move(double x, double y) async => true;
}

class _Input extends Fake implements InputModel {
  final clicks = <MouseButtons>[];
  final pressed = <MouseButtons>{};
  @override
  Future<void> tap(MouseButtons b) async => clicks.add(b);
  @override
  Future<void> tapDown(MouseButtons b) async {
    pressed.add(b);
  }

  @override
  Future<void> tapUp(MouseButtons b) async {
    pressed.remove(b);
  }
}

class _Canvas extends Fake implements CanvasModel {
  @override
  double scale = 1;
}

class _Wheel extends Fake implements RustdeskImpl {
  int distance = 0;
  @override
  Future<void> sessionSendMouse(
      {required SessionID sessionId, required String msg, dynamic hint}) async {
    final event = jsonDecode(msg);
    expect(event['type'], 'trackpad');
    distance += int.parse(event['y'] as String);
  }
}

class _Session extends Fake implements FFI {
  @override
  final sessionId = UuidValue('00000000-0000-4000-8000-000000000001');
  @override
  final canvasModel = _Canvas();
  @override
  final ffiModel = _Model();
  @override
  final cursorModel = _Cursor();
  @override
  final _Input inputModel = _Input();
}

void main() {
  testWidgets('swiping the same screen distance tracks the remote canvas scale',
      (tester) async {
    final wheel = _Wheel();
    platformFFI.setBindingForTesting(wheel);
    final session = _Session();
    session.ffiModel.pi
      ..phoneWorkspaceName = 'phone'
      ..displays.add(Display()..name = 'phone');
    await tester.pumpWidget(PhoneWorkspaceGestures(
        ffi: session, child: const ColoredBox(color: Color(0xff000000))));
    final center = tester.getCenter(find.byType(PhoneWorkspaceGestures));
    Future<int> drag(double scale) async {
      session.canvasModel.scale = scale;
      wheel.distance = 0;
      final finger = await tester.startGesture(center);
      await finger.moveBy(const Offset(0, -30));
      await finger.moveBy(const Offset(0, -100));
      final distance = -wheel.distance;
      await finger.up();
      await tester.pumpAndSettle();
      return distance;
    }
    final full = await drag(1);
    expect(full, greaterThanOrEqualTo(120));
    expect(await drag(.5), closeTo(full * 2, 1));
    expect(session.inputModel.clicks, isEmpty);
  });

  testWidgets(
      'holding and releasing opens the menu, holding and moving selects',
      (tester) async {
    final session = _Session();
    session.ffiModel.pi
      ..phoneWorkspaceName = 'phone'
      ..displays.add(Display()..name = 'phone');
    await tester.pumpWidget(PhoneWorkspaceGestures(
        ffi: session, child: const ColoredBox(color: Color(0xff000000))));
    final point = tester.getCenter(find.byType(PhoneWorkspaceGestures));
    await tester.longPressAt(point);
    await tester.pump();
    expect(session.inputModel.clicks, [MouseButtons.right]);
    final touch = await tester.startGesture(point);
    await tester.pump(const Duration(milliseconds: 600));
    await touch.moveBy(const Offset(60, 0));
    await tester.pump();
    expect(session.inputModel.pressed, {MouseButtons.left});
    await touch.up();
    await tester.pump();
    expect(session.inputModel.pressed, isEmpty);
    expect(session.inputModel.clicks, [MouseButtons.right]);
  });

  test('the physical screen keeps its input mapping after display reordering',
      () {
    final phone = Display()..name = 'phone';
    final physical = Display()..name = 'physical';
    final pi = PeerInfo()..phoneWorkspaceName = 'phone';
    pi.displays.addAll([phone, physical]);
    expect(pi.isPhoneWorkspace, isTrue);
    pi.displays.value = [physical, phone];
    expect(pi.isPhoneWorkspace, isFalse);
    pi.currentDisplay = 1;
    expect(pi.isPhoneWorkspace, isTrue);
  });
}
